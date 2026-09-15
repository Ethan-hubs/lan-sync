using System.Text.Json.Nodes;

namespace LanSync.Syncthing;

public sealed class VersionRestoreException : Exception
{
    public VersionRestoreException(JsonObject errors)
        : base($"Syncthing could not restore every requested version: {errors.ToJsonString()}")
    {
        Errors = (JsonObject)errors.DeepClone();
    }

    public JsonObject Errors { get; }
}
