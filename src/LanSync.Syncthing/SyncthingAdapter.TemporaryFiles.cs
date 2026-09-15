using LanSync.Core;
using System.Text.Json.Nodes;

namespace LanSync.Syncthing;

public sealed partial class SyncthingAdapter
{
    public async Task<IReadOnlyList<TemporaryFileEntry>> GetTemporaryFilesAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        var folder = await GetFolderAsync(folderId, cancellationToken).ConfigureAwait(false);
        var folderPath = folder["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new InvalidDataException($"Folder {folderId} did not contain a path.");
        }

        return await Task.Run(
            () => EnumerateTemporaryFiles(folderPath, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FolderDiagnostics> GetFolderDiagnosticsAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        var errors = await GetFolderErrorsAsync(folderId, cancellationToken).ConfigureAwait(false);
        var temporaryFiles = await GetTemporaryFilesAsync(folderId, cancellationToken).ConfigureAwait(false);
        return new FolderDiagnostics(errors, temporaryFiles);
    }

    private static IReadOnlyList<TemporaryFileEntry> EnumerateTemporaryFiles(
        string folderPath,
        CancellationToken cancellationToken)
    {
        var root = NormalizeExtendedPath(folderPath);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.PlatformDefault,
        };
        var result = new List<TemporaryFileEntry>();
        foreach (var path in Directory.EnumerateFiles(root, "~syncthing~*.tmp", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path);
                result.Add(new TemporaryFileEntry(
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    file.Length,
                    file.LastWriteTimeUtc));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Syncthing may remove a transient file between enumeration and metadata reads.
            }
        }

        return result.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string NormalizeExtendedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + fullPath[2..]
            : "\\\\?\\" + fullPath;
    }
}
