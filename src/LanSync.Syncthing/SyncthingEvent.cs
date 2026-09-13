using System.Text.Json.Nodes;

namespace LanSync.Syncthing;

public sealed record SyncthingEvent(long Id, DateTimeOffset? Time, string Type, JsonNode? Data);

