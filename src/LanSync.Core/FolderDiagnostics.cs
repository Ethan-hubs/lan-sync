namespace LanSync.Core;

public sealed record FolderDiagnostics(
    IReadOnlyList<FolderError> Errors,
    IReadOnlyList<TemporaryFileEntry> TemporaryFiles);
