namespace LanSync.Core;

public sealed record FolderSpec(
    string Id,
    string Label,
    string Path,
    IReadOnlyList<DeviceId> DeviceIds)
{
    public const int ThirtyDaysInSeconds = 30 * 24 * 60 * 60;
    public static readonly double DefaultFsWatcherDelaySeconds = 2;

    public string FolderType { get; init; } = "sendreceive";

    public double FsWatcherDelaySeconds { get; init; } = DefaultFsWatcherDelaySeconds;

    public int VersioningMaxAgeSeconds { get; init; } = ThirtyDaysInSeconds;
}

