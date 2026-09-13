using LanSync.Core;

namespace LanSync.Syncthing;

public sealed record ConnectionInfo(
    DeviceId DeviceId,
    bool Connected,
    bool Paused,
    string? Type,
    ConnectionKind Kind,
    string? Address,
    long? InBytesTotal,
    long? OutBytesTotal);

