namespace LanSync.Syncthing;

public sealed record HealthSnapshot(
    string? Version,
    string? DeviceId,
    long? UptimeSeconds,
    int? Goroutines,
    bool RestartRequired);

