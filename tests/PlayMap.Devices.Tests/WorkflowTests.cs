using System.Collections.Immutable;
using PlayMap.Core.Time;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Drivers.Christie;
using PlayMap.Devices.Drivers.Epson;
using PlayMap.Devices.Drivers.Panasonic;
using PlayMap.Devices.Drivers.PjLink;
using PlayMap.Devices.Drivers.Sony;
using PlayMap.Devices.Manager;
using PlayMap.Devices.Model;
using PlayMap.Devices.Tests.Simulators;
using PlayMap.Devices.Workflows;

namespace PlayMap.Devices.Tests;

/// <summary>The mapping workflows: the steps each plan holds per capability set (commands where the protocol has
/// the word, instructions where it does not or where it is gated), runs against simulated projectors, the black
/// frame's capture gate and settle times, identify, and the lens prior that never invents optics.</summary>
public class WorkflowTests
{
    private static readonly WorkflowRunner NoWait = new((_, _) => Task.CompletedTask);

    private static StepKind[] Kinds(WorkflowPlan p) => p.Steps.Select(s => s.Kind).ToArray();

    [Fact]
    public async Task PrepareForScanOnPjLinkCommandsShutterAndFreezeAndInstructsTheRest()
    {
        await using var d = new PjLinkDriver(TestSupport.Link(ProjectorProtocol.PjLink, 1), TestSupport.NoSecrets(), DeviceLog.None);
        var plan = MappingWorkflows.PrepareForScan(d);
        Assert.Equal([StepKind.Command, StepKind.Command, StepKind.Instruction, StepKind.Instruction, StepKind.Instruction], Kinds(plan));
        Assert.Contains("Reset keystone and warp", plan.Steps[3].Words, StringComparison.Ordinal);
        Assert.Contains("PJLink has no published command", plan.Steps[3].Words, StringComparison.Ordinal);
        Assert.Contains("Lock the lens", plan.Steps[4].Words, StringComparison.Ordinal);
        Assert.Equal(5, plan.Describe().Count);
        Assert.Equal(3, plan.Instructions.Count);
    }

    [Fact]
    public async Task PrepareForScanOnPanasonicIsCommandsAndALensRead()
    {
        await using var d = new PanasonicDriver(TestSupport.Link(ProjectorProtocol.PanasonicNtControl, 1), TestSupport.NoSecrets(), DeviceLog.None);
        var plan = MappingWorkflows.PrepareForScan(d);
        Assert.Equal([StepKind.Command, StepKind.Command, StepKind.Command, StepKind.Command, StepKind.Command, StepKind.Read], Kinds(plan));
        Assert.Equal(ProjectorCapabilities.EdgeBlend, plan.Steps[4].Uses);
        Assert.Empty(plan.Instructions);
    }

    [Fact]
    public async Task PrepareForScanOnChristieInstructsGeometryUntilTheLinkAllowsIt()
    {
        await using var gated = new ChristieDriver(TestSupport.Link(ProjectorProtocol.ChristieAscii, 1), TestSupport.NoSecrets(), DeviceLog.None);
        var plan = MappingWorkflows.PrepareForScan(gated);
        Assert.Equal(StepKind.Instruction, plan.Steps[3].Kind);
        Assert.Contains("unverified on this link", plan.Steps[3].Words, StringComparison.Ordinal);
        Assert.Equal(StepKind.Read, plan.Steps[^1].Kind);

        await using var allowed = new ChristieDriver(TestSupport.Link(ProjectorProtocol.ChristieAscii, 1, allowUnverified: true), TestSupport.NoSecrets(), DeviceLog.None);
        Assert.Equal(StepKind.Command, MappingWorkflows.PrepareForScan(allowed).Steps[3].Kind);
    }

    [Fact]
    public async Task PrepareForScanOnAGatedSonyLinkIsAllInstructions()
    {
        await using var d = new SonyAdcpDriver(TestSupport.Link(ProjectorProtocol.SonyAdcp, 1), TestSupport.NoSecrets(), DeviceLog.None);
        Assert.All(MappingWorkflows.PrepareForScan(d).Steps, s => Assert.Equal(StepKind.Instruction, s.Kind));
    }

    [Fact]
    public async Task PrepareForScanRunsOnAPanasonicAndRecordsTheLens()
    {
        await using var sim = new PanasonicSimulator { Power = "001", Shutter = "1", Freeze = "1", Pattern = "05", EdgeBlend = "+00001" };
        await using var d = new PanasonicDriver(TestSupport.Link(ProjectorProtocol.PanasonicNtControl, sim.Port), TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast);
        var outcome = await NoWait.RunAsync(d, MappingWorkflows.PrepareForScan(d), null, CancellationToken.None);
        Assert.True(outcome.AllSucceeded, string.Join("; ", outcome.Steps.Select(s => s.Result.ToString())));
        Assert.All(outcome.Steps.Take(5), s => Assert.Equal(CommandStage.Observed, s.Result.Stage));
        Assert.Equal(("0", "0", "00", "+00000", "+00000"), (sim.Shutter, sim.Freeze, sim.Pattern, sim.Geometry, sim.EdgeBlend));
        Assert.Equal(new LensPositions(-120, 300, null, 5), outcome.Lens);
    }

    [Fact]
    public async Task PrepareForScanRunsThroughTheManagersGate()
    {
        await using var sim = new PjLinkSimulator { Power = "1", Avmt = "31", Freeze = "1" };
        await using var m = new DeviceManager(new ProjectorDrivers(TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast), new ManualClock(), DeviceLog.None);
        await m.SetLinksAsync([TestSupport.Link(ProjectorProtocol.PjLink, sim.Port)]);
        var plan = MappingWorkflows.PrepareForScan(m.Driver("dv00000001")!);
        var outcome = await NoWait.RunAsync(m, plan, null, CancellationToken.None);
        Assert.True(outcome.AllSucceeded);
        Assert.Equal(("30", "0"), (sim.Avmt, sim.Freeze));
    }

    [Fact]
    public async Task ABlackFrameClosesSettlesCapturesOpensAndSettles()
    {
        await using var sim = new PanasonicSimulator { Power = "001" };
        await using var d = new PanasonicDriver(TestSupport.Link(ProjectorProtocol.PanasonicNtControl, sim.Port), TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast);
        var waits = new List<TimeSpan>();
        var runner = new WorkflowRunner((t, _) =>
        {
            waits.Add(t);
            return Task.CompletedTask;
        });
        string? shutterAtCapture = null;
        var timing = new ShutterTiming(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200));
        var plan = MappingWorkflows.BlackFrame(d, timing);
        Assert.Equal([StepKind.Command, StepKind.Capture, StepKind.Command], Kinds(plan));
        var outcome = await runner.RunAsync(d, plan, _ =>
        {
            shutterAtCapture = sim.Shutter;
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.True(outcome.AllSucceeded);
        Assert.Equal("1", shutterAtCapture);
        Assert.Equal("0", sim.Shutter);
        Assert.Equal([TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(1200)], waits);
    }

    [Fact]
    public async Task NoBlackFrameIsCapturedWhenTheShutterDidNotClose()
    {
        await using var sim = new EpsonSimulator();
        await using var d = new EpsonDriver(TestSupport.Link(ProjectorProtocol.EpsonEscVpNet, sim.Port), TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast);
        var captured = false;
        var outcome = await NoWait.RunAsync(d, MappingWorkflows.BlackFrame(d), _ =>
        {
            captured = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.False(captured);
        Assert.Equal(CommandStage.Rejected, outcome.Steps[0].Result.Stage);
        Assert.Equal(CommandStage.Refused, outcome.Steps[1].Result.Stage);
        Assert.Contains("not captured", outcome.Steps[1].Result.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAShutterTheBlackFrameIsVideoBlackAndSaysSo()
    {
        await using var d = new SonyAdcpDriver(TestSupport.Link(ProjectorProtocol.SonyAdcp, 1), TestSupport.NoSecrets(), DeviceLog.None);
        var plan = MappingWorkflows.BlackFrame(d);
        Assert.Equal([StepKind.Instruction, StepKind.Capture], Kinds(plan));
        Assert.Contains("video black", plan.Steps[0].Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentifyUsesTheProjectorsOwnPatternOrPlayMapsOutput()
    {
        await using var christie = new ChristieDriver(TestSupport.Link(ProjectorProtocol.ChristieAscii, 1), TestSupport.NoSecrets(), DeviceLog.None);
        var own = MappingWorkflows.Identify(christie, TimeSpan.FromSeconds(3));
        Assert.Equal([StepKind.Command, StepKind.Command], Kinds(own));
        Assert.Equal(TimeSpan.FromSeconds(3), own.Steps[0].Settle);
        await using var epson = new EpsonDriver(TestSupport.Link(ProjectorProtocol.EpsonEscVpNet, 1), TestSupport.NoSecrets(), DeviceLog.None);
        Assert.Equal([StepKind.Instruction], Kinds(MappingWorkflows.Identify(epson, TimeSpan.FromSeconds(3))));
        await using var pjlink = new PjLinkDriver(TestSupport.Link(ProjectorProtocol.PjLink, 1), TestSupport.NoSecrets(), DeviceLog.None);
        Assert.Contains("identification card", MappingWorkflows.Identify(pjlink, TimeSpan.FromSeconds(3)).Steps[0].Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentifyRunsTheChristieGridAndTurnsItOff()
    {
        await using var sim = new ChristieSimulator();
        await using var d = new ChristieDriver(TestSupport.Link(ProjectorProtocol.ChristieAscii, sim.Port), TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast);
        var outcome = await NoWait.RunAsync(d, MappingWorkflows.Identify(d, TimeSpan.FromSeconds(2)), null, CancellationToken.None);
        Assert.True(outcome.AllSucceeded);
        Assert.Contains("(ITP 1)", sim.Received);
        Assert.Equal(0, sim.Get("ITP"));
    }

    [Fact]
    public void ALensPriorWithoutACalibrationIsTheRawPositionsAndSaysWhy()
    {
        var raw = new LensPositions(-120, 300, 15, -7);
        var prior = MappingWorkflows.LensFromProjector(raw, ProjectorProtocol.ChristieAscii);
        Assert.Null(prior.ThrowRatio);
        Assert.Null(prior.ToIntrinsics(1920, 1200));
        Assert.Equal(raw, prior.Raw);
        Assert.Contains("no public document maps", prior.Words, StringComparison.Ordinal);
        Assert.Contains("no lens position was read", MappingWorkflows.LensFromProjector(LensPositions.Unknown, ProjectorProtocol.PjLink).Words, StringComparison.Ordinal);
    }

    [Fact]
    public void ALensCalibrationInterpolatesInsideItsSpanAndNeverExtrapolates()
    {
        var cal = LensCalibration.Empty
            .Add(new LensSample(new LensPositions(0, 0, 0, 0), 1.2, 0.0, 0.0))
            .Add(new LensSample(new LensPositions(1000, 2000, 1000, 0), 2.4, 0.2, 0.5));
        var prior = MappingWorkflows.LensFromProjector(new LensPositions(500, 1000, 500, 3), ProjectorProtocol.ChristieAscii, cal);
        Assert.Equal(1.8, prior.ThrowRatio!.Value, 9);
        Assert.Equal(0.1, prior.ShiftX!.Value, 9);
        Assert.Equal(0.25, prior.ShiftY!.Value, 9);
        var k = prior.ToIntrinsics(1920, 1080)!;
        Assert.Equal(1.8 * 1920, k.Fx, 6);
        Assert.Null(cal.ThrowRatioAt(1500));
        Assert.Null(cal.ShiftXAt(null));
        Assert.Equal(2, cal.Samples.Length);
        Assert.Equal(ImmutableArray<LensSample>.Empty, LensCalibration.Empty.Samples);
    }
}
