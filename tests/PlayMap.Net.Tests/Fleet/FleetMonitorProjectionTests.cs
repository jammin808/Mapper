using PlayMap.Core.Fleet;
using PlayMap.Net.Fleet;

namespace PlayMap.Net.Tests.Fleet;

/// <summary>What the fleet monitor tells its node when a peer's record moves: the board's lights and words, and the peer's place in a
/// hot-backup pair and a sync group — a primary lists its backup's mirror state, fences itself and finds its group's leader by them, so a
/// record that moves only those moves STATE too.</summary>
public sealed class FleetMonitorProjectionTests
{
    private static HealthRecord Backup(Func<HealthFacts, HealthFacts>? edit = null)
    {
        var facts = new HealthFacts
        {
            Node = "9f8e7d6c",
            Name = "FOH-B",
            AtUnixMs = 1_760_000_000_000,
            Role = FleetRoles.Backup,
            Of = "0badc0de",
            Mirror = "joining",
            SyncRole = "follower",
            SyncLeader = "0badc0de",
            SyncGroup = "show",
            Outputs = [new HealthOutput("op_1", "Left", true, 60, 0, 0, "gl-window")],
        };
        return HealthRecord.Of(edit is null ? facts : edit(facts));
    }

    [Fact]
    public void ARecordThatMovesOnlyItsPlaceInTheFleetIsToldOn()
    {
        var before = FleetMonitor.Projection(Backup());
        Assert.Equal(before, FleetMonitor.Projection(Backup(f => f with { AtUnixMs = f.AtUnixMs + 1000 })));
        Assert.NotEqual(before, FleetMonitor.Projection(Backup(f => f with { Mirror = "identical" })));
        Assert.NotEqual(before, FleetMonitor.Projection(Backup(f => f with { Epoch = 1 })));
        Assert.NotEqual(before, FleetMonitor.Projection(Backup(f => f with { SyncGroup = "other" })));
        Assert.NotEqual(before, FleetMonitor.Projection(Backup(f => f with { Role = FleetRoles.Primary })));
    }
}
