using PlayMap.Core.Fleet;
using PlayMap.Engine.Fleet;
using PlayMap.Net.Fleet;

namespace PlayMap.Engine;

/// <summary>wp2-fleet's services: the machine's counters, the other machines this node hears, the hub HEALTH is published through,
/// and the clock the fleet's pushes and liveness run on. A node without the fleet's part has none of the first three: its HEALTH
/// reads the machine's numbers −1 (grey) and its board holds itself alone.</summary>
public sealed partial record EngineServices
{
    public IMachineSampler? Machine { get; init; }

    public IFleetPeers? FleetPeers { get; init; }

    public HealthHub? HealthHub { get; init; }

    /// <summary>The hot backup: the mirror feed, the backup's link, the takeover and the fence (wp2-backup).</summary>
    public BackupNode? Backup { get; init; }

    /// <summary>HEALTH's once-a-second push and the board's liveness run on this clock (a test injects its own).</summary>
    public TimeProvider FleetTime { get; init; } = TimeProvider.System;
}
