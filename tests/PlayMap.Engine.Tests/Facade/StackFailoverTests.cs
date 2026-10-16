using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Manager;
using PlayMap.Devices.Tests.Simulators;
using PlayMap.Engine.Devices;
using PlayMap.Engine.Site;
using PlayMap.Engine.Tests.Fleet;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>
/// STACK FAILOVER (facade S2) on a hand-moved clock: a three-projector stack on a wall, blended (BLEND SOLVE) with a neighbour beside
/// it, under MATCH loses a member (its display output closes) — its picture is at 2/3 of its light, so the neighbour heads to 2/3,
/// reached 0.5 s later (and through the air's own gain), and the stack's remaining members stay at full (dimming them too would leave
/// the stack at 4/9 beside a neighbour at 2/3); composed with a degrade gain another part set before, never overwriting it; STATE, the
/// context, preflight and the journal name the cause; the member's return fades the neighbour back over 2 s. Under HOLD the loss is
/// told and nothing dims. A projector reporting a lamp error through its device link is a lost member too.
/// </summary>
public sealed class StackFailoverTests
{
    private const string P1 = "pj00000001", P2 = "pj00000002", P3 = "pj00000003", P4 = "pj00000004";

    private sealed class Fence : IOutputGains
    {
        public float GainOf(string outputId) => outputId == "op00000001" ? 0.5f : 1f;
    }

    private static Output Display(string id, string projectorId) =>
        StackKit.OutputFor(id, projectorId, 320, 180) with { Kind = OutputKind.Display, Blend = BlendSettings.Default with { Auto3D = true } };

    /// <summary>A 6 × 3 m wall; the stack's three projectors 4 m back aimed left of its middle, P4 beside them aimed right: each picture
    /// 4 m wide, the stack's and P4's sharing the middle 2.2 m.</summary>
    private static Project ThreeStack()
    {
        var k = Intrinsics.FromThrowRatio(320, 180, 1.0);
        var pose = Pose.LookAt(new Vec3d(-1.2, 0, 4), new Vec3d(-0.9, 0, 0), Vec3d.UnitY);
        Projector At(string id, string name, Pose p) => new(id, name, k, LensDistortion.None, p, 10_000, "#FFFFFF", false, [], SolveLocks.None, 0);
        var wall = new MeshAsset("ms_wall", "Wall", MeshOrigin.Primitive, "", "", "plane",
            ParamSet.Empty.With("width", 6.0).With("height", 3.0).With("segments", 12), 0, 0);
        var p = Project.CreateDefault("Failover");
        return p with
        {
            Outputs = [Display("op00000001", P1), Display("op00000002", P2), Display("op00000003", P3), Display("op00000004", P4)],
            Stage = p.Stage with
            {
                Meshes = [wall],
                Surfaces = [new Surface("sf_wall", "Wall", "ms_wall", Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#ffffff", [])],
                Projectors = [At(P1, "P1", pose), At(P2, "P2", pose), At(P3, "P3", pose), At(P4, "P4", Pose.LookAt(new Vec3d(1.2, 0, 4), new Vec3d(0.9, 0, 0), Vec3d.UnitY))],
                Stacks = [new StackGroup("sk00000001", "S1", [P1, P2, P3], P1, StackFailover.Match)],
            },
        };
    }

    /// <summary>BLEND SOLVE through the wire, waited for: the stack blends as one projector beside P4, so P4 is in its blend group.</summary>
    private static async Task Blended(EngineCore engine)
    {
        Assert.True(WireGrammar.TryParse("BLEND SOLVE", out var parsed, out _));
        Assert.True((await engine.HandleAsync(parsed, SiteRig.Desk, CancellationToken.None)).Ok);
        Assert.Equal("done", (await engine.WhenSolved.WaitAsync(TimeSpan.FromMinutes(2)))!.State);
        Assert.Contains("OP00000001", engine.LastBlendSolve!.Solution.For("op00000004")!.OverlapsWith);
    }

    private static async Task<string> Ok(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        var r = await engine.HandleAsync(parsed, SiteRig.Desk, CancellationToken.None);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return r.Payload ?? "";
    }

    private static JsonElement Stack(EngineCore engine) =>
        JsonDocument.Parse(engine.State().Json).RootElement.GetProperty("stage").GetProperty("stacks")[0].Clone();

    [Fact]
    public async Task AMatchedStackThatLosesAMemberDimsItsGroupToTheLightThatRemainsAndFadesBack()
    {
        var clock = new ManualClock(5_000_000_000);
        var outputs = new HealthTests.Outputs { Now = [.. new[] { 1, 2, 3, 4 }.Select(i => new OutputHealth($"op0000000{i}", true, 60, 0, 4, $"DP-{i}"))] };
        var services = new SiteExtension().Configure(EngineServices.ForTests(clock) with { Outputs = outputs, OutputGains = new Fence() });
        Assert.IsType<ComposedGains>(services.OutputGains);
        await using var engine = new EngineCore(services, ThreeStack());
        var gains = services.OutputGains!;
        await Blended(engine);

        await engine.Lane.RunAsync(engine.WatchStacks);
        Assert.Equal(0, Stack(engine).GetProperty("degraded").GetArrayLength());
        Assert.Equal(0.5f, gains.GainOf("op00000001"));

        // P3's display closes: a measured fault.
        outputs.Now = [.. outputs.Now.Select(h => h.OutputId == "op00000003" ? h with { Open = false } : h)];
        await engine.Lane.RunAsync(engine.WatchStacks);
        var stack = Stack(engine);
        Assert.Equal([P3], stack.GetProperty("degraded").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(0.667, stack.GetProperty("gain").GetDouble());
        // The neighbour's fade starts where its gain is and reaches 2/3 0.5 s later; the stack's remaining members give 2/3 of its
        // light already and stay at full; the fence another part set stays multiplied in.
        Assert.Equal(1f, gains.GainOf("op00000004"));
        clock.AdvanceMs(250);
        Assert.InRange(gains.GainOf("op00000004"), 0.82f, 0.85f);
        clock.AdvanceMs(250);
        Assert.Equal(2f / 3, gains.GainOf("op00000004"), 4);
        Assert.Equal(1f, gains.GainOf("op00000002"));
        Assert.Equal(0.5f, gains.GainOf("op00000001"));
        Assert.Equal(2f / 3, engine.AirGain("op00000004", engine.LeaderNowNs), 4);
        Assert.Equal(1f, engine.AirGain("op00000002", engine.LeaderNowNs));

        var item = ContextBuilder.Build(engine).Items.Single(i => i.Id == "stack:sk00000001");
        Assert.Equal(Light.Red, item.Light);
        Assert.Contains("S1 lost P3 — matched: the building at 67 %", item.State, StringComparison.Ordinal);
        var journal = engine.Journal.Recent(20).Where(r => r.Kind == "stack").ToList();
        Assert.Contains(journal, r => r.Text.Contains("S1 lost P3 (OP00000003 is not open)", StringComparison.Ordinal) && r.Text.Contains("67 %", StringComparison.Ordinal));
        var rows = await new SitePreflight().RunAsync(new PreflightContext(engine, PreflightDepth.Quick, false), CancellationToken.None);
        var row = Assert.Single(rows, r => r.Id == "stack:sk00000001");
        Assert.Equal(Light.Red, row.Light);
        Assert.Contains("dimmed to 67 %", row.Note, StringComparison.Ordinal);

        // A second look changes nothing and journals nothing.
        var before = engine.Journal.Recent(50).Count;
        await engine.Lane.RunAsync(engine.WatchStacks);
        Assert.Equal(before, engine.Journal.Recent(50).Count);

        // P3 is back: the neighbour returns to full over 2 s.
        outputs.Now = [.. outputs.Now.Select(h => h with { Open = true })];
        await engine.Lane.RunAsync(engine.WatchStacks);
        Assert.Equal(0, Stack(engine).GetProperty("degraded").GetArrayLength());
        Assert.Equal(1.0, Stack(engine).GetProperty("gain").GetDouble());
        Assert.Equal(2f / 3, gains.GainOf("op00000004"), 4);
        clock.AdvanceMs(1000);
        Assert.Equal(5f / 6, gains.GainOf("op00000004"), 3);
        clock.AdvanceMs(1000);
        Assert.Equal(1f, gains.GainOf("op00000004"));
        Assert.Contains(engine.Journal.Recent(20), r => r.Kind == "stack" && r.Text == "S1: P3 is back — the rest of its blend group returns to full over 2 s");
        Assert.Equal(Light.Amber, ContextBuilder.Build(engine).Items.Single(i => i.Id == "stack:sk00000001").Light);
    }

    [Fact]
    public async Task UnderHoldTheLossIsToldAndNothingDims()
    {
        var clock = new ManualClock(5_000_000_000);
        var outputs = new HealthTests.Outputs { Now = [.. new[] { 1, 2, 3, 4 }.Select(i => new OutputHealth($"op0000000{i}", i != 2, 60, 0, 4, $"DP-{i}"))] };
        var services = new SiteExtension().Configure(EngineServices.ForTests(clock) with { Outputs = outputs });
        await using var engine = new EngineCore(services, ThreeStack());
        await Blended(engine);
        var said = JsonDocument.Parse(await Ok(engine, "STACK FAILOVER S1 HOLD")).RootElement;
        Assert.Equal("hold", said.GetProperty("failover").GetString());
        Assert.Equal(StackFailover.Hold, engine.Store.Project.Stage.Stacks[0].Failover);
        clock.AdvanceMs(3000);
        var stack = Stack(engine);
        Assert.Equal([P2], stack.GetProperty("degraded").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(1.0, stack.GetProperty("gain").GetDouble());
        Assert.Equal(1f, services.OutputGains!.GainOf("op00000004"));
        Assert.Contains("held", ContextBuilder.Build(engine).Items.Single(i => i.Id == "stack:sk00000001").State, StringComparison.Ordinal);
        var row = Assert.Single(await new SitePreflight().RunAsync(new PreflightContext(engine, PreflightDepth.Quick, false), CancellationToken.None),
            r => r.Id == "stack:sk00000001");
        Assert.Equal("STACK FAILOVER sk00000001 MATCH", row.Fix);

        // MATCH again: applied at once from the verb, to the neighbour; the stack's remaining members stay at full.
        await Ok(engine, "STACK FAILOVER S1 MATCH");
        clock.AdvanceMs(500);
        Assert.Equal(2f / 3, services.OutputGains.GainOf("op00000004"), 4);
        Assert.Equal(1f, services.OutputGains.GainOf("op00000001"));
        var again = await engine.HandleAsync(Parse("STACK FAILOVER S1 MATCH"), SiteRig.Desk, CancellationToken.None);
        Assert.False(again.Ok);
        Assert.Contains("would change nothing", again.Payload, StringComparison.Ordinal);
    }

    /// <summary>The hot backup's fence and a stack's MATCH gain through the one degrade gain the air reads, with the backup's own
    /// <see cref="PlayMap.Engine.Fleet.FenceGains"/>, composed both ways round (the node composes the fleet's part before the site's; a fence composed after
    /// takes what came before as its inner gain): a fenced node (FENCE, or the primary a FAILOVER took the show from) is black on every
    /// output whatever its stack's gain, and the stack is still measured and told; lifting the fence (FAILBACK, the show handed back)
    /// leaves every output at the stack's gain; a member back fades the neighbour up again while the node holds the show.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFencedNodeIsBlackWhateverItsStackSaysAndTheStacksGainHoldsOnceTheFenceLifts(bool fleetFirst)
    {
        var clock = new ManualClock(5_000_000_000);
        var outputs = new HealthTests.Outputs { Now = [.. new[] { 1, 2, 3, 4 }.Select(i => new OutputHealth($"op0000000{i}", true, 60, 0, 4, $"DP-{i}"))] };
        var fence = new PlayMap.Engine.Fleet.FenceGains(null);
        var basis = EngineServices.ForTests(clock) with { Outputs = outputs };
        EngineServices services;
        if (fleetFirst)
        {
            // The node's order (EngineParts): the fleet's part sets its fence with nothing before it, the site's composes after it.
            services = new SiteExtension().Configure(basis with { OutputGains = fence });
            var composed = Assert.IsType<ComposedGains>(services.OutputGains);
            Assert.Same(fence, composed.First);
            Assert.Same(services.StackGains, composed.Second);
        }
        else
        {
            var site = new SiteExtension().Configure(basis);
            fence.Inner = site.OutputGains;
            services = site with { OutputGains = fence };
        }
        await using var engine = new EngineCore(services, ThreeStack());
        float Air(string output) => engine.AirGain(output, engine.LeaderNowNs);
        await Blended(engine);

        // Fenced, with a member lost: every output black, the stack measured, dimmed underneath and told.
        fence.Held = true;
        outputs.Now = [.. outputs.Now.Select(h => h.OutputId == "op00000003" ? h with { Open = false } : h)];
        await engine.Lane.RunAsync(engine.WatchStacks);
        clock.AdvanceMs(500);
        Assert.All(new[] { "op00000001", "op00000002", "op00000003", "op00000004" }, o => Assert.Equal(0f, Air(o)));
        Assert.Equal([P3], Stack(engine).GetProperty("degraded").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(0.667, Stack(engine).GetProperty("gain").GetDouble());
        Assert.Equal(2f / 3, services.StackGains!.GainOf("op00000004"), 4);

        // The fence lifted (the show handed back): the neighbour at the light that remains, the stack's own members at full.
        fence.Held = false;
        Assert.Equal(1f, Air("op00000001"));
        Assert.Equal(1f, Air("op00000002"));
        Assert.Equal(2f / 3, Air("op00000004"), 4);

        // Fenced again (a FAILOVER took the show) as the member comes back: black throughout, the neighbour fading up underneath.
        fence.Held = true;
        outputs.Now = [.. outputs.Now.Select(h => h with { Open = true })];
        await engine.Lane.RunAsync(engine.WatchStacks);
        clock.AdvanceMs(1000);
        Assert.Equal(0f, Air("op00000004"));
        Assert.Equal(5f / 6, services.StackGains.GainOf("op00000004"), 3);
        fence.Held = false;
        clock.AdvanceMs(1000);
        Assert.Equal(1f, Air("op00000004"));
        Assert.Equal(0, Stack(engine).GetProperty("degraded").GetArrayLength());
    }

    private static WireLine Parse(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out _));
        return line!;
    }

    [Fact]
    public async Task AProjectorReportingALampErrorIsALostMember()
    {
        await using var projector = new PjLinkSimulator { Power = "1" };
        var credentials = new NodeCredentialStore(Path.Combine(Directory.CreateTempSubdirectory("playmap-keys-").FullName, "keys"));
        await using var devices = new DeviceService(credentials, MonoClock.Instance, new DeviceServiceOptions
        {
            PjLinkUdpPort = -1,
            Timings = DriverTimings.Fast,
            Manager = new DeviceManagerOptions { TickMs = 50 },
        });
        var clock = new ManualClock(5_000_000_000);
        var services = new SiteExtension().Configure(EngineServices.ForTests(clock) with { Devices = devices });
        await using var engine = new EngineCore(services, ThreeStack());
        await using var attached = devices.Attach(engine);
        await Blended(engine);
        var id = (await Ok(engine, $"DEVICE ADD Third pjlink 127.0.0.1:{projector.Port} PROJECTOR {P3}")).Trim();
        // The device's own poll reads it; a loaded machine can take a while, so the wait is long and ends at the read.
        Assert.True(await OpsRig.Eventually(() => devices.State(id) is { Online: true, FaultsRead: true }, 60_000), "the link's first full read");
        await engine.Lane.RunAsync(engine.WatchStacks);
        Assert.Equal(0, Stack(engine).GetProperty("degraded").GetArrayLength());

        projector.Erst = "020000";
        Assert.True(await OpsRig.Eventually(() => devices.State(id)?.Faults.Any(f => f.Severity == PlayMap.Devices.Model.FaultSeverity.Error) == true, 60_000),
            "the lamp error read");
        await engine.Lane.RunAsync(engine.WatchStacks);
        Assert.Equal([P3], Stack(engine).GetProperty("degraded").EnumerateArray().Select(d => d.GetString()));
        Assert.Contains(engine.Journal.Recent(20), r => r.Kind == "stack" && r.Text.StartsWith("S1 lost P3 (Third: ", StringComparison.Ordinal));
        clock.AdvanceMs(500);
        Assert.Equal(2f / 3, services.OutputGains!.GainOf("op00000004"), 4);
        Assert.Equal(1f, services.OutputGains.GainOf("op00000001"));
    }
}

internal static class GainsExtensions
{
    public static float GetOf(this IOutputGains gains, string outputId) => gains.GainOf(outputId);
}
