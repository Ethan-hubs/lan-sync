namespace LanSync.Core;

public sealed record TemporaryFileEntry(
    string RelativePath,
    long Length,
    DateTimeOffset LastWriteTimeUtc);
