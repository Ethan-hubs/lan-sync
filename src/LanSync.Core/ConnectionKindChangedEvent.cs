namespace LanSync.Core;

public sealed record ConnectionKindChangedEvent(
    DeviceId DeviceId,
    ConnectionKind OldKind,
    ConnectionKind NewKind,
    DateTimeOffset Timestamp);
