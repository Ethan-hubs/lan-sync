using System.Text.Json.Nodes;
using LanSync.Core;

namespace LanSync.Syncthing;

public sealed partial class SyncthingAdapter
{
    public async Task<IReadOnlyDictionary<string, ConnectionInfo>> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var response = RequireObject(
            await RestClient.GetAsync("/rest/system/connections", cancellationToken).ConfigureAwait(false),
            "connections");
        var connections = response["connections"] as JsonObject ?? new JsonObject();
        var result = new Dictionary<string, ConnectionInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (deviceIdText, value) in connections)
        {
            if (value is not JsonObject connection)
            {
                continue;
            }

            var connected = connection["connected"]?.GetValue<bool>() ?? false;
            var paused = connection["paused"]?.GetValue<bool>() ?? false;
            var type = connection["type"]?.GetValue<string>();
            var kind = MapConnectionKind(connected, type);
            var deviceId = new DeviceId(deviceIdText);
            result[deviceIdText] = new ConnectionInfo(
                deviceId,
                connected,
                paused,
                type,
                kind,
                connection["address"]?.GetValue<string>(),
                connection["inBytesTotal"]?.GetValue<long>(),
                connection["outBytesTotal"]?.GetValue<long>());
        }

        return result;
    }

    public async Task<ConnectionInfo> GetConnectionAsync(DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var connections = await GetConnectionsAsync(cancellationToken).ConfigureAwait(false);
        return connections.TryGetValue(deviceId.Value, out var connection)
            ? connection
            : new ConnectionInfo(deviceId, false, false, null, ConnectionKind.Offline, null, null, null);
    }

    public async Task<ConnectionInfo> WaitForConnectionAsync(
        DeviceId deviceId,
        TimeSpan timeout,
        ConnectionKind? expectedKind = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        ConnectionInfo? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await GetConnectionAsync(deviceId, cancellationToken).ConfigureAwait(false);
            if (last.Connected && (expectedKind is null || last.Kind == expectedKind))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Connection to {deviceId} did not reach the expected state. Last state: {last}.");
    }

    internal static ConnectionKind MapConnectionKind(bool connected, string? type)
    {
        if (!connected)
        {
            return ConnectionKind.Offline;
        }

        if (type is not null &&
            (type.StartsWith("tcp-", StringComparison.OrdinalIgnoreCase) ||
             type.StartsWith("quic-", StringComparison.OrdinalIgnoreCase)))
        {
            return ConnectionKind.Direct;
        }

        if (type is not null && type.StartsWith("relay-", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionKind.Relay;
        }

        return ConnectionKind.Unknown;
    }
}
