namespace LanSync.Core;

public sealed record VersionEntry(string Path, string VersionTime, long? Size = null, string? ModTime = null);

