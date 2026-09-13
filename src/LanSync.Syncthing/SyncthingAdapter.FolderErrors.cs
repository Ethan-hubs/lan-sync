using System.Text.Json.Nodes;
using LanSync.Core;

namespace LanSync.Syncthing;

public sealed partial class SyncthingAdapter
{
    public async Task<IReadOnlyList<FolderError>> GetFolderErrorsAsync(
        string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var path = "/rest/folder/errors" + (string.IsNullOrWhiteSpace(folderId) ? string.Empty : $"?folder={Escape(folderId)}");
        var response = await RestClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        var errors = response switch
        {
            JsonArray directArray => directArray,
            JsonObject objectResponse when objectResponse["errors"] is JsonArray nestedArray => nestedArray,
            null => new JsonArray(),
            _ => throw new InvalidDataException("Syncthing folder errors returned an unexpected JSON response."),
        };

        return errors.OfType<JsonObject>()
            .Select(error => new FolderError(
                error["path"]?.GetValue<string>() ?? string.Empty,
                error["error"]?.GetValue<string>() ?? error["message"]?.GetValue<string>() ?? string.Empty))
            .ToArray();
    }
}
