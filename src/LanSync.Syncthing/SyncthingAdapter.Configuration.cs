using System.Text.Json.Nodes;

namespace LanSync.Syncthing;

public sealed partial class SyncthingAdapter
{
    public async Task<JsonObject> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
        RequireObject(await RestClient.GetAsync("/rest/config", cancellationToken).ConfigureAwait(false), "config");

    public async Task<JsonObject> GetOptionsAsync(CancellationToken cancellationToken = default) =>
        RequireObject(await RestClient.GetAsync("/rest/config/options", cancellationToken).ConfigureAwait(false), "options");

    public async Task<bool> IsRestartRequiredAsync(CancellationToken cancellationToken = default)
    {
        var response = RequireObject(
            await RestClient.GetAsync("/rest/config/restart-required", cancellationToken).ConfigureAwait(false),
            "restart-required");
        return response["requiresRestart"]?.GetValue<bool>() ?? false;
    }

    public async Task<ConfigurationUpdateResult> UpdateOptionsAsync(
        IReadOnlyDictionary<string, JsonNode?> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var current = await GetOptionsAsync(cancellationToken).ConfigureAwait(false);
        var changedProperties = new List<string>();

        foreach (var (name, requestedValue) in changes)
        {
            if (JsonNode.DeepEquals(current[name], requestedValue))
            {
                continue;
            }

            current[name] = requestedValue?.DeepClone();
            changedProperties.Add(name);
        }

        if (changedProperties.Count > 0)
        {
            await RestClient.PutAsync("/rest/config/options", current, cancellationToken).ConfigureAwait(false);
        }

        return new ConfigurationUpdateResult(
            changedProperties,
            await IsRestartRequiredAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<ConfigurationUpdateResult> UpdateConfigurationAsync(
        IReadOnlyDictionary<string, JsonNode?> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var current = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var changedProperties = ApplyChanges(current, changes);
        if (changedProperties.Count > 0)
        {
            await RestClient.PutAsync("/rest/config", current, cancellationToken).ConfigureAwait(false);
        }

        return new ConfigurationUpdateResult(
            changedProperties,
            await IsRestartRequiredAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task RestartIfRequiredAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!await IsRestartRequiredAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await RestClient.PostAsync("/rest/system/restart", cancellationToken: cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        await WaitUntilReadyAsync(timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> ApplyChanges(JsonObject target, IReadOnlyDictionary<string, JsonNode?> changes)
    {
        var changedProperties = new List<string>();
        foreach (var (name, requestedValue) in changes)
        {
            if (JsonNode.DeepEquals(target[name], requestedValue))
            {
                continue;
            }

            target[name] = requestedValue?.DeepClone();
            changedProperties.Add(name);
        }

        return changedProperties;
    }
}
