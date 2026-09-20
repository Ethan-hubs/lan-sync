namespace LanSync.Core;

public sealed record RestorePresentation(
    string Summary,
    string? Warning,
    bool RequiresWarning,
    IReadOnlyList<DeviceId> UnpausedDevices);

public static class RestorePresentationPolicy
{
    public const string UnpausedPeerWarning = "对端未暂停，若其在线可能产生冲突副本";

    public static RestorePresentation Create(VersionRestoreResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            return new RestorePresentation(
                "本地恢复未完成",
                "Syncthing 未确认本地恢复完成。",
                RequiresWarning: true,
                result.UnpausedDevices);
        }

        if (!result.PeerPaused)
        {
            return new RestorePresentation(
                "已在本地执行恢复",
                UnpausedPeerWarning,
                RequiresWarning: true,
                result.UnpausedDevices);
        }

        if (!result.DurabilityVerified)
        {
            return new RestorePresentation(
                "已在本地执行恢复",
                "恢复结果尚未通过对端覆盖风险校验。",
                RequiresWarning: true,
                result.UnpausedDevices);
        }

        return new RestorePresentation(
            "已恢复成功",
            Warning: null,
            RequiresWarning: false,
            result.UnpausedDevices);
    }
}
