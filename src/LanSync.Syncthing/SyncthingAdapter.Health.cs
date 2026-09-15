using System.Text.Json.Nodes;
using LanSync.Core;

namespace LanSync.Syncthing;

public sealed partial class SyncthingAdapter
{
    public async Task<JsonObject> PingAsync(CancellationToken cancellationToken = default) =>
        RequireObject(await RestClient.GetAsync("/rest/system/ping", cancellationToken).ConfigureAwait(false), "ping");

    public async Task<DeviceId> GetLocalDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        var status = RequireObject(
            await RestClient.GetAsync("/rest/system/status", cancellationToken).ConfigureAwait(false),
            "status");
        return new DeviceId(status["myID"]?.GetValue<string>()
            ?? throw new InvalidDataException("Syncthing status did not contain myID."));
    }

    public async Task<HealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var version = RequireObject(await RestClient.GetAsync("/rest/system/version", cancellationToken).ConfigureAwait(false), "version");
        var status = RequireObject(await RestClient.GetAsync("/rest/system/status", cancellationToken).ConfigureAwait(false), "status");
        var restart = RequireObject(await RestClient.GetAsync("/rest/config/restart-required", cancellationToken).ConfigureAwait(false), "restart-required");

        return new HealthSnapshot(
            version["version"]?.GetValue<string>(),
            status["myID"]?.GetValue<string>(),
            status["uptime"]?.GetValue<long>(),
            status["goroutines"]?.GetValue<int>(),
            restart["requiresRestart"]?.GetValue<bool>() ?? false);
    }

    public async Task WaitUntilReadyAsync(
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await PingAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SyncthingApiException exception)
            {
                lastError = exception;
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Syncthing did not become ready within {timeout}.", lastError);
    }

    private static JsonObject RequireObject(JsonNode? node, string operation) =>
        node as JsonObject ?? throw new InvalidDataException($"Syncthing {operation} returned a non-object JSON response.");
}

