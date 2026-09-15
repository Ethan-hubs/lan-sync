using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using LanSync.Core;

namespace LanSync.Syncthing;

public sealed partial class SyncthingAdapter
{
    internal Task<JsonNode?> RestoreVersionRawAsync(
        string folderId,
        string relativePath,
        string versionTime,
        CancellationToken cancellationToken = default) =>
        RestClient.PostAsync(
            $"/rest/folder/versions?folder={Escape(folderId)}",
            new JsonObject { [relativePath] = versionTime },
            cancellationToken);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<VersionEntry>>> GetVersionsAsync(
        string folderId,
        string? relativePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        var path = $"/rest/folder/versions?folder={Escape(folderId)}";
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            path += $"&file={Escape(relativePath)}";
        }

        var response = RequireObject(
            await RestClient.GetAsync(path, cancellationToken).ConfigureAwait(false),
            "folder versions");
        var result = new Dictionary<string, IReadOnlyList<VersionEntry>>(StringComparer.Ordinal);
        foreach (var (filePath, node) in response)
        {
            if (node is not JsonArray versions)
            {
                continue;
            }

            result[filePath] = versions.OfType<JsonObject>()
                .Select(version => new VersionEntry(
                    filePath,
                    version["versionTime"]?.GetValue<string>() ?? string.Empty,
                    version["size"]?.GetValue<long>(),
                    version["modTime"]?.GetValue<string>()))
                .Where(version => !string.IsNullOrWhiteSpace(version.VersionTime))
                .ToArray();
        }

        return result;
    }

    public async Task<VersionRestoreResult> RestoreVersionAsync(
        string folderId,
        string relativePath,
        string versionTime,
        IReadOnlyDictionary<DeviceId, SyncthingAdapter> peerAdapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionTime);
        ArgumentNullException.ThrowIfNull(peerAdapters);

        var folder = await GetFolderAsync(folderId, cancellationToken).ConfigureAwait(false);
        var localDeviceId = await GetLocalDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        var availableVersions = await GetVersionsAsync(folderId, relativePath, cancellationToken).ConfigureAwait(false);
        var selectedVersion = availableVersions.GetValueOrDefault(relativePath)?.FirstOrDefault(entry =>
            string.Equals(entry.VersionTime, versionTime, StringComparison.Ordinal));
        if (selectedVersion is null || string.IsNullOrWhiteSpace(selectedVersion.ModTime))
        {
            throw new InvalidDataException(
                $"Version {versionTime} for {relativePath} is unavailable or has no modTime.");
        }

        await WaitUntilFolderIdleAsync(folderId, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var peersToPause = new List<(DeviceId Id, SyncthingAdapter Adapter)>();
        foreach (var device in (folder["devices"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var deviceIdText = device["deviceID"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(deviceIdText) ||
                string.Equals(deviceIdText, localDeviceId.Value, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var peerId = new DeviceId(deviceIdText);
            var peerAdapter = peerAdapters.FirstOrDefault(pair =>
                string.Equals(pair.Key.Value, peerId.Value, StringComparison.OrdinalIgnoreCase)).Value;
            if (peerAdapter is null)
            {
                throw new ArgumentException(
                    $"No Adapter was supplied for peer {peerId}, which shares folder {folderId}.",
                    nameof(peerAdapters));
            }

            var localDeviceOnPeer = await peerAdapter.GetDeviceAsync(localDeviceId, cancellationToken)
                .ConfigureAwait(false);
            var isPaused = localDeviceOnPeer["paused"]?.GetValue<bool>() ?? false;
            if (!isPaused && peersToPause.All(peer =>
                    !string.Equals(peer.Id.Value, peerId.Value, StringComparison.OrdinalIgnoreCase)))
            {
                peersToPause.Add((peerId, peerAdapter));
            }
        }

        var pausedByTransaction = new List<(DeviceId Id, SyncthingAdapter Adapter)>();
        var resumedByTransaction = new List<DeviceId>();
        var resumeErrors = new List<Exception>();
        Exception? operationError = null;
        try
        {
            try
            {
                foreach (var peer in peersToPause)
                {
                    await peer.Adapter.PauseAsync(localDeviceId, cancellationToken).ConfigureAwait(false);
                    pausedByTransaction.Add(peer);
                    await Task.WhenAll(
                        WaitForDisconnectionAsync(peer.Id, TimeSpan.FromSeconds(15), cancellationToken),
                        peer.Adapter.WaitForDisconnectionAsync(
                            localDeviceId,
                            TimeSpan.FromSeconds(15),
                            cancellationToken)).ConfigureAwait(false);
                }

                if (pausedByTransaction.Count > 0)
                {
                    // The v2.1.5 puller can still finish queued work briefly after both REST
                    // connection views become offline. The validated C7 procedure uses this
                    // settle window before touching the version archive.
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }

                var body = new JsonObject { [relativePath] = versionTime };
                var response = await RestClient.PostAsync(
                    $"/rest/folder/versions?folder={Escape(folderId)}",
                    body,
                    cancellationToken).ConfigureAwait(false);
                if (response is not JsonObject restoreErrors)
                {
                    throw new InvalidDataException(
                        "Syncthing version restore returned a non-object response; success requires an empty error dictionary.");
                }

                if (restoreErrors.Count != 0)
                {
                    throw new VersionRestoreException(restoreErrors);
                }

                await WaitUntilVersionConsumedAsync(
                    folderId,
                    relativePath,
                    versionTime,
                    TimeSpan.FromSeconds(15),
                    cancellationToken).ConfigureAwait(false);
                await WaitUntilRestoredFileIndexedAsync(
                    folderId,
                    relativePath,
                    selectedVersion.ModTime,
                    TimeSpan.FromSeconds(15),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                operationError = exception;
            }
        }
        finally
        {
            foreach (var peer in pausedByTransaction.AsEnumerable().Reverse())
            {
                try
                {
                    await peer.Adapter.ResumeAsync(localDeviceId, CancellationToken.None).ConfigureAwait(false);
                    resumedByTransaction.Add(peer.Id);
                }
                catch (Exception exception)
                {
                    resumeErrors.Add(exception);
                }
            }
        }

        if (operationError is not null && resumeErrors.Count > 0)
        {
            throw new AggregateException([operationError, .. resumeErrors]);
        }

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        if (resumeErrors.Count > 0)
        {
            throw new AggregateException(resumeErrors);
        }

        return new VersionRestoreResult(
            folderId,
            relativePath,
            versionTime,
            pausedByTransaction.Select(peer => peer.Id).ToArray(),
            resumedByTransaction.ToArray(),
            Succeeded: true);
    }

    private async Task WaitForDisconnectionAsync(
        DeviceId deviceId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var connection = await GetConnectionAsync(deviceId, cancellationToken).ConfigureAwait(false);
            if (!connection.Connected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Device {deviceId} did not disconnect after pause within {timeout}.");
    }

    private async Task WaitUntilFolderIdleAsync(
        string folderId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? lastState = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await GetFolderStatusAsync(folderId, cancellationToken).ConfigureAwait(false);
            lastState = status["state"]?.GetValue<string>();
            if (string.Equals(lastState, "idle", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Folder {folderId} did not become idle within {timeout}; last state was {lastState ?? "<missing>"}.");
    }

    private async Task WaitUntilVersionConsumedAsync(
        string folderId,
        string relativePath,
        string versionTime,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var versions = await GetVersionsAsync(folderId, relativePath, cancellationToken).ConfigureAwait(false);
            if (!versions.TryGetValue(relativePath, out var entries) ||
                entries.All(entry => !string.Equals(entry.VersionTime, versionTime, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Restored version {versionTime} for {relativePath} remained available after {timeout}.");
    }

    private async Task WaitUntilRestoredFileIndexedAsync(
        string folderId,
        string relativePath,
        string expectedModTime,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? lastIndexedModTime = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var file = RequireObject(
                await RestClient.GetAsync(
                    $"/rest/db/file?folder={Escape(folderId)}&file={Escape(relativePath)}",
                    cancellationToken).ConfigureAwait(false),
                "database file");
            lastIndexedModTime = (file["local"] as JsonObject)?["modified"]?.GetValue<string>();
            if (TimesRepresentSameInstant(lastIndexedModTime, expectedModTime))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Restored file {relativePath} was not indexed with modTime {expectedModTime} within {timeout}; " +
            $"last indexed modTime was {lastIndexedModTime ?? "<missing>"}.");
    }

    private static bool TimesRepresentSameInstant(string? actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return true;
        }

        return DateTimeOffset.TryParse(actual, out var actualTime) &&
            DateTimeOffset.TryParse(expected, out var expectedTime) &&
            Math.Abs((actualTime - expectedTime).TotalSeconds) < 1;
    }
}
