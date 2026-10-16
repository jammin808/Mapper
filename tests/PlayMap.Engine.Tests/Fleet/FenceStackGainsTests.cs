using PlayMap.Core.Fleet;
using PlayMap.Engine.Fleet;
using PlayMap.Engine.Host;
using PlayMap.Engine.Site;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>A node composed as the exe composes it — the fleet's part before the site's (EngineParts.Standard) — carries one degrade
/// gain the air reads: the hot backup's own fence over the stacks' gains. A node that starts fenced is black on every output whatever a
/// stack's gain says; with the fence lifted the air shows the stack's gain, and an output no stack dims stays at full.</summary>
public sealed class FenceStackGainsTests
{
    [Fact]
    public async Task TheFenceIsComposedOverTheStacksGainsAndAFencedNodeIsBlackWhateverTheyAre()
    {
        var standard = EngineParts.Standard().Extensions.ToList();
        var fleetAt = standard.IndexOf(FleetExtension.Factory);
        Assert.True(fleetAt >= 0, "the exe composes the fleet's part");
        Assert.True(standard.IndexOf(SiteExtension.Factory) > fleetAt, "the site's part comes after the fleet's");

        var options = new FleetOptions { Time = new ManualTime(), Reader = new UnknownMachineReader("not read in tests") };
        var parts = new EngineParts { Extensions = [setup => new FleetExtension(setup, options), SiteExtension.Factory] };
        await using var node = await HostRig.StartAsync("Fenced", parts: parts,
            settings: s => s with { Fleet = s.Fleet with { Role = FleetRoles.Fenced, Monitor = false } });
        var engine = node.Engine;
        var backup = Assert.IsType<BackupNode>(engine.Services.Backup);
        var composed = Assert.IsType<ComposedGains>(engine.Services.OutputGains);
        Assert.Same(backup.Gains, composed.First);
        Assert.Same(engine.Services.StackGains, composed.Second);
        Assert.True(backup.Gains.Held);

        // A stack's MATCH dims one output of its group to two thirds; the node is fenced: black everywhere.
        engine.Services.StackGains!.Set(new Dictionary<string, float>(StringComparer.Ordinal) { ["op_2"] = 2f / 3 });
        Assert.Equal(2f / 3, engine.Services.StackGains.TargetOf("op_2"));
        Assert.Equal(0f, engine.AirGain("op_1", engine.LeaderNowNs));
        Assert.Equal(0f, engine.AirGain("op_2", engine.LeaderNowNs));

        // The fence lifted (the show handed back): the stack's gain is the air's once its fade has run; the other output is at full.
        backup.Gains.Held = false;
        Assert.Equal(1f, engine.AirGain("op_1", engine.LeaderNowNs));
        Assert.True(await Eventually.TrueAsync(() => Math.Abs(engine.AirGain("op_2", engine.LeaderNowNs) - 2f / 3) < 1e-4, 10_000),
            $"op_2 at {engine.AirGain("op_2", engine.LeaderNowNs)}");
    }
}
