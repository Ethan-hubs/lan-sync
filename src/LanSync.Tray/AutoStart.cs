using System;
using System.IO;
using LanSync.Core;
using Microsoft.Win32;

namespace LanSync.Tray;

// 开机自启仅响应当前用户的菜单操作；启动阶段只清理失效项，不创建注册表值。
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LanSync";

    public static bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKey);
        return runKey?.GetValue(RunValueName) is string;
    }

    public static void RepairStaleEntry()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        var runCommand = runKey?.GetValue(RunValueName) as string;
        if (runCommand is null)
        {
            return;
        }

        var executablePath = AutoStartPolicy.GetExecutablePath(runCommand);
        var executableExists = executablePath is not null && File.Exists(executablePath);
        if (AutoStartPolicy.DecideStartup(runCommand, executableExists) == AutoStartAction.Delete)
        {
            runKey!.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        var action = AutoStartPolicy.DecideMenuChange(enabled);
        if (action == AutoStartAction.Write)
        {
            var executablePath = Environment.ProcessPath ??
                throw new InvalidOperationException("无法确定当前托盘程序路径，不能启用开机自启。");
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKey);
            runKey.SetValue(RunValueName, AutoStartPolicy.FormatRunCommand(executablePath));
        }
        else
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            runKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }
}
