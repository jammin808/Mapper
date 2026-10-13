using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;
using PlayMap.Engine;

namespace PlayMap.Engine.Tests;

/// <summary>The context's links use only the verbs Patterns' Eye draws: a link of any other kind would be dropped there silently.</summary>
public sealed class ContextLinkTests
{
    /// <summary>A network with both links live.</summary>
    private sealed class LinkedNetwork : INetworkView
    {
        public IReadOnlyList<PeerView> Peers() => [];
        public LinkView? Patterns() => new("live", "10.0.0.5:9697", true, "");
        public LinkView? Stagewatch() => new("live", "10.0.0.6:9923", true, "timer");
        public string? StagewatchTimerJson() => null;
        public SyncView Sync() => new("follower", "0badc0de", 0.2, 1, 0);
    }

    [Fact]
    public async Task EveryLinkTheContextEmitsIsAnEyeVerb()
    {
        var p = Project.CreateDefault("Show");
        var projector = new Projector("pj0a0b0c01", "Left", Intrinsics.FromThrowRatio(1920, 1080, 1.5, 0, 0.5), LensDistortion.None,
            Pose.LookAt(new Vec3d(0, 1.5, -6), new Vec3d(0, 1.5, 0), Vec3d.UnitY), 12000, "#FF8800", false, [], SolveLocks.None, 0.8);
        p = p with
        {
            Stage = p.Stage with
            {
                Projectors = [projector],
                Devices = [new DeviceLink("dv0a0b0c01", "Left box", "pjlink", "10.0.0.21", 0, "", projector.Id, "op_1", 0, false)],
            },
            Outputs = p.Outputs.SetItem(0, p.Outputs[0] with { Mode = OutputMode.Projector, ProjectorId = projector.Id }),
        };
        var services = EngineServices.ForTests(new ManualClock(5_000_000_000)) with { Network = new LinkedNetwork() };
        await using var engine = new EngineCore(services, p);

        var context = ContextBuilder.Build(engine);

        Assert.Contains(context.Links, l => l.Kind == "drives");
        Assert.Contains(context.Links, l => l.From == "link:patterns");
        Assert.Contains(context.Links, l => l.From == "device:dv0a0b0c01");
        Assert.All(context.Links, l => Assert.True(ContextLinkKinds.IsEyeVerb(l.Kind), $"{l.From} → {l.To}: {l.Kind}"));
        Assert.Equal(11, ContextLinkKinds.All.Count);
        Assert.False(ContextLinkKinds.IsEyeVerb("Drives"));
        Assert.False(ContextLinkKinds.IsEyeVerb("plays"));
    }
}
