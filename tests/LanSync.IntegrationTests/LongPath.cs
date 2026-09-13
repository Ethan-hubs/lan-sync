namespace LanSync.IntegrationTests;

internal static class LongPath
{
    public static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    public static void CreateDirectory(string path) => Directory.CreateDirectory(Normalize(path));

    public static void WriteAllText(string path, string contents) => File.WriteAllText(Normalize(path), contents);

    public static bool Exists(string path) => File.Exists(Normalize(path));

    public static string ReadAllText(string path) => File.ReadAllText(Normalize(path));

    public static void DeleteFile(string path)
    {
        var normalized = Normalize(path);
        if (File.Exists(normalized))
        {
            File.Delete(normalized);
        }
    }

    public static void DeleteTree(string path)
    {
        var normalized = Normalize(path);
        if (Directory.Exists(normalized))
        {
            Directory.Delete(normalized, recursive: true);
        }
    }
}
