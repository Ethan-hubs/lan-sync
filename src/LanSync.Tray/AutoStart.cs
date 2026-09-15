using System;
using Microsoft.Win32;

namespace LanSync.Tray;

// 开机自启由托盘自身负责（ADR-001 §14(4)）：提权安装脚本写 HKCU 会落到管理员配置单元，
// 故此处以当前用户上下文自写/自校验，且菜单可关闭。
internal static class AutoStart
{
    private const string AppKey = @"Software\LanSync";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LanSync";

    public static bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKey);
        return runKey?.GetValue(RunValueName) is string;
    }

    public static void EnsureInitialized()
    {
        using var key = Registry.CurrentUser.CreateSubKey(AppKey);
        if (key.GetValue("AutoStart") is int)
        {
            return; // 用户已配置过（开或关），尊重其选择，不再强制改写。
        }

        SetEnabled(true);
    }

    public static void SetEnabled(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? string.Empty;
            runKey.SetValue(RunValueName, $"\"{exePath}\"");
        }
        else
        {
            runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
        }

        using var key = Registry.CurrentUser.CreateSubKey(AppKey);
        key.SetValue("AutoStart", enabled ? 1 : 0, RegistryValueKind.DWord);
    }
}