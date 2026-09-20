namespace LanSync.Core;

public sealed record VersionRestoreResult(
    string FolderId,
    string RelativePath,
    string VersionTime,
    IReadOnlyList<DeviceId> PausedDevices,
    IReadOnlyList<DeviceId> ResumedDevices,
    bool PeerPaused,
    IReadOnlyList<DeviceId> UnpausedDevices,
    bool Succeeded);
