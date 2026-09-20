using System;
using System.IO;

namespace LanSync.Tray;

internal static class EngineSettings
{
    public const string BaseAddress = "http://127.0.0.1:8384";

    public static string? ApiKey { get; } = ReadApiKey();

    private static string? ReadApiKey()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("LANSW_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "LanSync",
                "api-key.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
