using System.Text.Json.Nodes;
using LanSync.Core;

namespace LanSync.Syncthing;

public sealed partial class SyncthingAdapter
{
    public async Task<JsonArray> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        RequireArray(await RestClient.GetAsync("/rest/config/devices", cancellationToken).ConfigureAwait(false), "devices");

    public Task<JsonNode?> AddDeviceAsync(
        DeviceId deviceId,
        string name,
        IReadOnlyList<string>? addresses = null,
        bool paused = false,
        bool introducer = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var addressArray = new JsonArray();
        foreach (var address in addresses ?? ["dynamic"])
        {
            addressArray.Add(address);
        }

        var body = new JsonObject
        {
            ["deviceID"] = deviceId.Value,
            ["name"] = name,
            ["addresses"] = addressArray,
            ["paused"] = paused,
            ["introducer"] = introducer,
            ["compression"] = "metadata",
        };
        return RestClient.PostAsync("/rest/config/devices", body, cancellationToken);
    }

    public async Task<JsonObject> GetDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken = default) =>
        RequireObject(
            await RestClient.GetAsync($"/rest/config/devices/{Escape(deviceId.Value)}", cancellationToken).ConfigureAwait(false),
            "device");

    public async Task UpdateDeviceAsync(DeviceId deviceId, JsonObject changes, CancellationToken cancellationToken = default)
    {
        var current = await GetDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (MergeChanges(current, changes))
        {
            await RestClient.PutAsync($"/rest/config/devices/{Escape(deviceId.Value)}", current, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<JsonNode?> DeleteDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken = default) =>
        RestClient.DeleteAsync($"/rest/config/devices/{Escape(deviceId.Value)}", cancellationToken);

    public async Task<JsonArray> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        RequireArray(await RestClient.GetAsync("/rest/config/folders", cancellationToken).ConfigureAwait(false), "folders");

    public Task<JsonNode?> AddFolderAsync(FolderSpec spec, CancellationToken cancellationToken = default) =>
        RestClient.PostAsync("/rest/config/folders", CreateFolderConfiguration(spec), cancellationToken);

    public async Task<JsonObject> GetFolderAsync(string folderId, CancellationToken cancellationToken = default) =>
        RequireObject(
            await RestClient.GetAsync($"/rest/config/folders/{Escape(folderId)}", cancellationToken).ConfigureAwait(false),
            "folder");

    public async Task UpdateFolderAsync(string folderId, JsonObject changes, CancellationToken cancellationToken = default)
    {
        var current = await GetFolderAsync(folderId, cancellationToken).ConfigureAwait(false);
        if (MergeChanges(current, changes))
        {
            await RestClient.PutAsync($"/rest/config/folders/{Escape(folderId)}", current, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<JsonNode?> DeleteFolderAsync(string folderId, CancellationToken cancellationToken = default) =>
        RestClient.DeleteAsync($"/rest/config/folders/{Escape(folderId)}", cancellationToken);

    public async Task<IReadOnlyList<string>> GetIgnoresAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var response = RequireObject(
            await RestClient.GetAsync($"/rest/db/ignores?folder={Escape(folderId)}", cancellationToken).ConfigureAwait(false),
            "ignores");
        return response["ignore"] is JsonArray ignores
            ? ignores.Select(node => node?.GetValue<string>() ?? string.Empty).ToArray()
            : [];
    }

    public Task<JsonNode?> SetIgnoresAsync(
        string folderId,
        IReadOnlyList<string> patterns,
        CancellationToken cancellationToken = default)
    {
        var ignores = new JsonArray();
        foreach (var pattern in patterns)
        {
            ignores.Add(pattern);
        }

        return RestClient.PostAsync(
            $"/rest/db/ignores?folder={Escape(folderId)}",
            new JsonObject { ["ignore"] = ignores },
            cancellationToken);
    }

    public Task<JsonNode?> RescanAsync(string folderId, string? subPath = null, CancellationToken cancellationToken = default)
    {
        var path = $"/rest/db/scan?folder={Escape(folderId)}";
        if (!string.IsNullOrWhiteSpace(subPath))
        {
            path += $"&sub={Escape(subPath)}";
        }

        return RestClient.PostAsync(path, cancellationToken: cancellationToken);
    }

    public async Task<JsonObject> GetFolderStatusAsync(string folderId, CancellationToken cancellationToken = default) =>
        RequireObject(
            await RestClient.GetAsync($"/rest/db/status?folder={Escape(folderId)}", cancellationToken).ConfigureAwait(false),
            "folder status");

    private static JsonObject CreateFolderConfiguration(FolderSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var devices = new JsonArray();
        foreach (var deviceId in spec.DeviceIds)
        {
            devices.Add(new JsonObject
            {
                ["deviceID"] = deviceId.Value,
                ["introducedBy"] = string.Empty,
                ["encryptionPassword"] = string.Empty,
            });
        }

        return new JsonObject
        {
            ["id"] = spec.Id,
            ["label"] = spec.Label,
            ["path"] = spec.Path,
            ["type"] = spec.FolderType,
            ["devices"] = devices,
            ["rescanIntervalS"] = 3600,
            ["fsWatcherEnabled"] = true,
            ["fsWatcherDelayS"] = spec.FsWatcherDelaySeconds,
            ["versioning"] = new JsonObject
            {
                ["type"] = "staggered",
                ["params"] = new JsonObject
                {
                    ["maxAge"] = spec.VersioningMaxAgeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                ["cleanupIntervalS"] = 3600,
            },
        };
    }

    private static bool MergeChanges(JsonObject target, JsonObject changes)
    {
        var changed = false;
        foreach (var (name, value) in changes)
        {
            if (JsonNode.DeepEquals(target[name], value))
            {
                continue;
            }

            target[name] = value?.DeepClone();
            changed = true;
        }

        return changed;
    }

    private static JsonArray RequireArray(JsonNode? node, string operation) =>
        node as JsonArray ?? throw new InvalidDataException($"Syncthing {operation} returned a non-array JSON response.");
}
