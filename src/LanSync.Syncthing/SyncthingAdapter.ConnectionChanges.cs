using System.Runtime.CompilerServices;
using LanSync.Core;

namespace LanSync.Syncthing;

public sealed partial class SyncthingAdapter
{
    public static readonly TimeSpan DefaultConnectionObservationInterval = TimeSpan.FromSeconds(1);

    public async IAsyncEnumerable<ConnectionKindChangedEvent> SubscribeConnectionChangesAsync(
        TimeSpan? pollInterval = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        bool emitInitialSnapshot = false)
    {
        var interval = pollInterval ?? DefaultConnectionObservationInterval;
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "The poll interval must be positive.");
        }

        var previous = await GetConnectionsAsync(cancellationToken).ConfigureAwait(false);
        if (emitInitialSnapshot)
        {
            var snapshotTimestamp = DateTimeOffset.UtcNow;
            foreach (var connection in previous.Values.OrderBy(item => item.DeviceId.Value, StringComparer.OrdinalIgnoreCase))
            {
                yield return new ConnectionKindChangedEvent(
                    connection.DeviceId,
                    ConnectionKind.Unknown,
                    GetObservableKind(connection),
                    snapshotTimestamp,
                    IsInitialSnapshot: true);
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            var current = await GetConnectionsAsync(cancellationToken).ConfigureAwait(false);
            var changes = CompareConnectionKinds(previous, current, DateTimeOffset.UtcNow);
            previous = current;

            foreach (var change in changes)
            {
                yield return change;
            }
        }
    }

    private static IReadOnlyList<ConnectionKindChangedEvent> CompareConnectionKinds(
        IReadOnlyDictionary<string, ConnectionInfo> previous,
        IReadOnlyDictionary<string, ConnectionInfo> current,
        DateTimeOffset timestamp)
    {
        var deviceIds = previous.Keys.Concat(current.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(deviceId => deviceId, StringComparer.OrdinalIgnoreCase);
        var changes = new List<ConnectionKindChangedEvent>();
        foreach (var deviceIdText in deviceIds)
        {
            var oldKind = previous.TryGetValue(deviceIdText, out var oldConnection)
                ? GetObservableKind(oldConnection)
                : ConnectionKind.Offline;
            var newKind = current.TryGetValue(deviceIdText, out var newConnection)
                ? GetObservableKind(newConnection)
                : ConnectionKind.Offline;
            if (oldKind == newKind)
            {
                continue;
            }

            var deviceId = newConnection?.DeviceId ?? oldConnection!.DeviceId;
            changes.Add(new ConnectionKindChangedEvent(deviceId, oldKind, newKind, timestamp));
        }

        return changes;
    }

    private static ConnectionKind GetObservableKind(ConnectionInfo connection) =>
        connection.Paused ? ConnectionKind.Paused : connection.Kind;
}
