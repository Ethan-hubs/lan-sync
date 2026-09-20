namespace LanSync.Core;

public enum AutoStartAction
{
    None,
    Write,
    Delete,
}

public static class AutoStartPolicy
{
    public static AutoStartAction DecideStartup(string? runCommand, bool executableExists)
    {
        if (runCommand is null)
        {
            return AutoStartAction.None;
        }

        return executableExists ? AutoStartAction.None : AutoStartAction.Delete;
    }

    public static AutoStartAction DecideMenuChange(bool enabled) =>
        enabled ? AutoStartAction.Write : AutoStartAction.Delete;

    public static string? GetExecutablePath(string? runCommand)
    {
        if (string.IsNullOrWhiteSpace(runCommand))
        {
            return null;
        }

        var command = runCommand.Trim();
        if (command[0] == '"')
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 ? command[1..closingQuote] : null;
        }

        var separator = command.IndexOfAny([' ', '\t']);
        return separator < 0 ? command : command[..separator];
    }

    public static string FormatRunCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{executablePath}\"";
    }
}
