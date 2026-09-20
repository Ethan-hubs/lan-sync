namespace LanSync.Core;

public enum TrayStatusLight
{
    Offline,
    Direct,
    Relay,
    Paused,
    AuthError,
}

public static class TrayStatusPolicy
{
    public static TrayStatusLight FromConnectionKind(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Direct => TrayStatusLight.Direct,
        ConnectionKind.Relay => TrayStatusLight.Relay,
        ConnectionKind.Paused => TrayStatusLight.Paused,
        _ => TrayStatusLight.Offline,
    };

    public static TrayStatusLight Aggregate(IEnumerable<ConnectionKind> connectionKinds)
    {
        ArgumentNullException.ThrowIfNull(connectionKinds);
        var kinds = connectionKinds.ToArray();
        if (kinds.Length == 0)
        {
            return TrayStatusLight.Offline;
        }

        if (kinds.All(kind => kind == ConnectionKind.Paused))
        {
            return TrayStatusLight.Paused;
        }

        if (kinds.Contains(ConnectionKind.Direct))
        {
            return TrayStatusLight.Direct;
        }

        if (kinds.Contains(ConnectionKind.Relay))
        {
            return TrayStatusLight.Relay;
        }

        return TrayStatusLight.Offline;
    }
}
