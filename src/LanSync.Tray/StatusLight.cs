using System.Collections.Generic;
using System.Linq;
using LanSync.Core;
using LanSync.Syncthing;

namespace LanSync.Tray;

public enum StatusLight
{
    Offline,
    Direct,
    Relay,
    Paused,
    // 阶段二：授权异常，仅保留色位，阶段一不取值。
    AuthError,
}

public static class StatusLightMapper
{
    public static StatusLight FromConnection(bool paused, ConnectionKind kind)
    {
        if (paused)
        {
            return StatusLight.Paused;
        }

        return kind switch
        {
            ConnectionKind.Direct => StatusLight.Direct,
            ConnectionKind.Relay => StatusLight.Relay,
            _ => StatusLight.Offline,
        };
    }

    public static StatusLight Aggregate(IReadOnlyList<ConnectionInfo> connections)
    {
        if (connections.Count == 0)
        {
            return StatusLight.Offline;
        }

        if (connections.All(connection => connection.Paused))
        {
            return StatusLight.Paused;
        }

        var connected = connections.Where(connection => connection.Connected).ToArray();
        if (connected.Any(connection => connection.Kind == ConnectionKind.Direct))
        {
            return StatusLight.Direct;
        }

        if (connected.Any(connection => connection.Kind == ConnectionKind.Relay))
        {
            return StatusLight.Relay;
        }

        return StatusLight.Offline;
    }
}