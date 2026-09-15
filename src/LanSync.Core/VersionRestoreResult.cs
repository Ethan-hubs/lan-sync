namespace LanSync.Core;

public sealed record VersionRestoreResult(IReadOnlyList<DeviceId> PausedDevices);
