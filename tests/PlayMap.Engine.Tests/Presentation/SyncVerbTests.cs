using System.Diagnostics;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Presentation;
using PlayMap.Core.Sync;
using PlayMap.Core.Time;
using PlayMap.Engine.Presentation;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;
using PlayMap.Render.Sync;

namespace PlayMap.Engine.Tests.Presentation;

/// <summary>SYNC APPLY, LEAVE and REJOIN: every refusal by its reason field (the reply leads with it and STATE's sync.lock.refused
/// keeps it), and what each applied verb observed.</summary>
public sealed class SyncVerbTests
{
    private static async Task<string> Refused(PresentRig rig, string line, string reason)
    {
        var r = await rig.Run(line);
        Assert.False(r.Ok, $"{line} was not refused");
        Assert.StartsWith(reason + ": ", r.Payload, StringComparison.Ordinal);
        var refused = rig.State().GetProperty("sync").GetProperty("lock").GetProperty("refused");
        Assert.Equal(reason, refused.GetProperty("reason").GetString());
        Assert.Equal(string.Join(' ', line.Split(' ').Take(2)), refused.GetProperty("verb").GetString());
        Assert.False(string.IsNullOrWhiteSpace(refused.GetProperty("words").GetString()));
        return r.Payload!;
    }

    private static PresentRig Rig(ScriptedOutputs? outputs = null, Project? project = null, INetworkView? network = null) =>
        PresentRig.Start(project ?? PresentRig.With(PresentRig.Screen("op_a"), PresentRig.Screen("op_b")),
            outputs ?? new ScriptedOutputs().WithFreeLocks("op_a", "op_b"), network);

    [Fact]
    public async Task ANodeWithoutARenderHostRefusesEveryLockVerb()
    {
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")), scripted: false);
        await Refused(rig, "SYNC APPLY", "no-render");
        await Refused(rig, "SYNC LEAVE", "no-render");
        await Refused(rig, "SYNC REJOIN", "no-render");
    }

    [Fact]
    public async Task AFollowerLeavesTheApplyToItsLeader()
    {
        await using var rig = Rig(network: new RoleNetwork("follower", "9f8e7d6c"));
        var words = await Refused(rig, "SYNC APPLY", "follower");
        Assert.Contains("SYNC APPLY on 9f8e7d6c", words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArmedRefusesApplyAndRejoinUnlessBlackedOut()
    {
        await using var rig = Rig();
        rig.Gate.Armed = true;
        await Refused(rig, "SYNC APPLY", LockReasons.Armed);
        Assert.DoesNotContain(rig.Outputs.Calls, c => c.StartsWith("apply", StringComparison.Ordinal));

        Assert.True((await rig.Run("BLACKOUT ON")).Ok);
        var applied = await rig.Run("SYNC APPLY");
        Assert.True(applied.Ok, applied.Payload);
        Assert.True((await rig.Run("BLACKOUT OFF")).Ok);

        rig.Outputs.LockViews["op_a"] = rig.Outputs.LockViews["op_a"] with { State = LockState.FellOut, Reason = LockReasons.Stall };
        await Refused(rig, "SYNC REJOIN op_a", LockReasons.Armed);
        Assert.True((await rig.Run("BLACKOUT ON")).Ok);
        Assert.True((await rig.Run("SYNC REJOIN op_a")).Ok);
    }

    [Fact]
    public async Task NoBoardANotConfiguredBoardAndACableIntoASwitchAreRefusedByTheirReasons()
    {
        await using var rig = Rig();
        var board = rig.Outputs.BoardFacts!;
        rig.Outputs.BoardFacts = null;
        await Refused(rig, "SYNC APPLY", LockReasons.NoBoard);
        rig.Outputs.BoardFacts = board with { Configured = false };
        Assert.Contains("administrator", await Refused(rig, "SYNC APPLY", LockReasons.NotConfigured), StringComparison.Ordinal);
        rig.Outputs.BoardFacts = board with { CableIntoSwitch = true };
        Assert.Contains("network switch", await Refused(rig, "SYNC APPLY", LockReasons.CableIntoSwitch), StringComparison.Ordinal);
        Assert.Empty(rig.Outputs.Calls);
    }

    [Fact]
    public async Task MixedRatesNoOutputsAndAnUnknownGroupAreRefused()
    {
        await using (var mixed = Rig(project: PresentRig.With(PresentRig.Screen("op_a", 60), PresentRig.Screen("op_b", 50))))
        {
            var words = await Refused(mixed, "SYNC APPLY", "mixed-rates");
            Assert.Contains("OP_A at 60 Hz", words, StringComparison.Ordinal);
            Assert.Contains("OP_B at 50 Hz", words, StringComparison.Ordinal);
        }
        await using (var none = Rig(project: PresentRig.With(PresentRig.Screen("op_a") with { Enabled = false })))
            await Refused(none, "SYNC APPLY", "no-outputs");
        await using (var group = Rig())
            await Refused(group, "SYNC APPLY nowhere", "unknown-group");
    }

    [Fact]
    public async Task AnApplyAnswersEachOutputsObservedLockAndASecondIsANoOpRefusal()
    {
        await using var rig = Rig();
        var r = await rig.Run("SYNC APPLY");
        Assert.True(r.Ok, r.Payload);
        Assert.Equal("apply 1 1", Assert.Single(rig.Outputs.Calls));
        using (var doc = JsonDocument.Parse(r.Payload!))
        {
            Assert.Equal("applied", doc.RootElement.GetProperty("did").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("locked").GetInt32());
            Assert.All(doc.RootElement.GetProperty("outputs").EnumerateArray(), o => Assert.Equal("framelocked", o.GetProperty("state").GetString()));
        }
        await Refused(rig, "SYNC APPLY", "already");
        var s = rig.State();
        Assert.Equal("framelocked", s.GetProperty("sync").GetProperty("lock").GetProperty("state").GetString());
        Assert.Equal("framelocked", s.GetProperty("outputs")[0].GetProperty("lock").GetProperty("state").GetString());
    }

    [Fact]
    public async Task ANodeWhoseFrameLockIsOffRefusesToJoin()
    {
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")), new ScriptedOutputs().WithFreeLocks("op_a"),
            settings: s => s with { Present = s.Present with { FrameLock = "off" } });
        var words = await Refused(rig, "SYNC APPLY", "off");
        Assert.Contains("NodeSettings.Present.FrameLock", words, StringComparison.Ordinal);
        await Refused(rig, "SYNC REJOIN op_a", "off");
        Assert.Empty(rig.Outputs.Calls);
        Assert.False(rig.State().GetProperty("sync").GetProperty("lock").GetProperty("applied").GetBoolean());
    }

    [Fact]
    public async Task LeaveAndRejoinRefuseWhatWouldChangeNothing()
    {
        await using var rig = Rig();
        await Refused(rig, "SYNC LEAVE", "not-applied");
        await Refused(rig, "SYNC REJOIN", "not-applied");
        Assert.True((await rig.Run("SYNC APPLY")).Ok);
        await Refused(rig, "SYNC REJOIN", "nothing-fell-out");
        await Refused(rig, "SYNC LEAVE op_x", "no-output");
        await Refused(rig, "SYNC REJOIN op_x", "no-output");
        Assert.DoesNotContain(rig.Outputs.Calls, c => c.Contains("op_x", StringComparison.Ordinal));

        var left = await rig.Run("SYNC LEAVE op_b");
        Assert.True(left.Ok, left.Payload);
        Assert.Contains("leave op_b", rig.Outputs.Calls);
        await Refused(rig, "SYNC LEAVE op_b", "not-in-lock");
        var rejoined = await rig.Run("SYNC REJOIN op_b");
        Assert.True(rejoined.Ok, rejoined.Payload);
        Assert.Contains("rejoin op_b", rig.Outputs.Calls);

        var all = await rig.Run("SYNC LEAVE ALL");
        Assert.True(all.Ok, all.Payload);
        Assert.Contains("leave all", rig.Outputs.Calls);
        Assert.False(rig.State().GetProperty("sync").GetProperty("lock").GetProperty("applied").GetBoolean());
    }
}

/// <summary>The lock machine on the real render host (headless, GL on Xvfb) with a scripted board: apply, observe framelocked; a
/// stalled count falls out at three periods and the output keeps presenting; the preflight row is red with its fix; a rejoin locks again.</summary>
[Collection("render host")]
public sealed class FrameLockHostTests
{
    /// <summary>A headless host on a scripted board; outputs that fell out stay out until SYNC REJOIN (no rejoin by itself), so the
    /// test decides when they join again.</summary>
    private static (HostRig Rig, FakeFrameLockDriver Driver) Start(params Output[] outputs)
    {
        var driver = new FakeFrameLockDriver();
        var rig = HostRig.Start(HostRig.WithOutputs(outputs),
            options: RenderHostOptions.Default with { Windows = false, FrameLock = () => driver, FrameLockAuto = false });
        return (rig, driver);
    }

    private static Output Screen(string id) => HostRig.Screen(id, SourceRef.Solid("#336699"), 64, 36);

    private static JsonElement State(HostRig rig)
    {
        rig.Engine.FactsChanged();
        using var doc = JsonDocument.Parse(rig.Engine.State().Json);
        return doc.RootElement.Clone();
    }

    private static async Task Until(HostRig rig, Func<bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < HostRig.Patience, what);
            await rig.Host.FramesAsync(2).WaitAsync(HostRig.Patience);
        }
    }

    [Fact]
    public async Task ApplyLocksEveryOutputAStallFallsOutAndARejoinLocksAgain()
    {
        var (rig, driver) = Start(Screen("op_a"), Screen("op_b"));
        await using var _rig = rig;
        await Until(rig, () => rig.Host.Board is not null && rig.Host.Locks().Count == 2, "the board and both outputs were never seen");

        var applied = await rig.Run("SYNC APPLY");
        Assert.True(applied.Ok, applied.Payload);
        using (var doc = JsonDocument.Parse(applied.Payload!))
            Assert.Equal(2, doc.RootElement.GetProperty("locked").GetInt32());
        Assert.Contains("join op_a", driver.Calls);
        var s = State(rig);
        Assert.Equal("framelocked", s.GetProperty("sync").GetProperty("lock").GetProperty("state").GetString());
        Assert.Equal("Quadro Sync II (P2061)", s.GetProperty("sync").GetProperty("lock").GetProperty("board").GetString());
        Assert.All(s.GetProperty("outputs").EnumerateArray(), o => Assert.Equal("framelocked", o.GetProperty("lock").GetProperty("state").GetString()));

        // The barrier stalls: the count stands still, the machine leaves within three refreshes, and the outputs keep presenting.
        var events = new List<ShowEvent>();
        rig.Engine.EventRaised += e => { lock (events) events.Add(e); };
        var presentedBefore = rig.Host.Present().Single(p => p.OutputId == "op_a").Present.Presented;
        driver.Stalled = true;
        await Until(rig, () => rig.Host.Locks()["op_a"].State == LockState.FellOut, "op_a never fell out");
        driver.Stalled = false;
        Assert.Equal(LockReasons.CountStalled, rig.Host.Locks()["op_a"].Reason);
        Assert.Contains("leave op_a", driver.Calls);
        await rig.Host.FramesAsync(10).WaitAsync(HostRig.Patience);
        Assert.True(rig.Host.Present().Single(p => p.OutputId == "op_a").Present.Presented >= presentedBefore + 10, "the output stopped presenting");
        await Until(rig, () => { lock (events) return events.Any(e => e.Kind == "sync.fellout"); }, "no sync.fellout event");
        Assert.Contains(rig.Engine.Journal.Recent(50), j => j.Kind == "fault" && j.Text.Contains("fell out of the frame lock", StringComparison.Ordinal));
        s = State(rig);
        Assert.Equal("fell-out", s.GetProperty("sync").GetProperty("lock").GetProperty("state").GetString());
        Assert.Equal("fell-out", s.GetProperty("outputs")[0].GetProperty("lock").GetProperty("state").GetString());
        Assert.Equal(LockReasons.CountStalled, s.GetProperty("outputs")[0].GetProperty("lock").GetProperty("reason").GetString());

        // Preflight: red, with the line that fixes it.
        var rows = await new OutputPresentCheck().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        var row = Assert.Single(rows, r => r.Id == "outputs.lock.op_a");
        Assert.Equal(Core.Bus.Light.Red, row.Light);
        Assert.Equal("SYNC REJOIN op_a", row.Fix);

        // The fixed line, run: op_a locks again (both outputs fell out together on the one stalled board).
        var rejoin = await rig.Run(row.Fix!);
        Assert.True(rejoin.Ok, rejoin.Payload);
        await Until(rig, () => rig.Host.Locks()["op_a"].Locked, "op_a never locked again");
        Assert.Equal(1, rig.Host.Locks()["op_a"].Rejoins);
        Assert.Equal(1, rig.Host.Locks()["op_a"].FellOut);
    }

    /// <summary>A presenter that returns as a vsync'd swap does, just after the next vblank of a 60 Hz grid on the monotonic clock,
    /// and holds every tenth present (each lasts at least that long whatever the machine's load). On the GL contract a held present
    /// returns four periods late (a software rasteriser, a display server's round trip, a starved thread); on the DXGI flip contract
    /// (<c>DxgiOutputWindow</c>) a held present is a full queue — <c>Present1(DO_NOT_WAIT)</c> answers at once that it queued nothing
    /// — and every fifteenth waits out the waitable's cap of three periods.</summary>
    private sealed class HeldPresenter(PresentPath path) : IOutputPresenter
    {
        private const double Period = 1e9 / 60;
        private readonly long _start = MonoClock.Instance.NowNs;
        private int _presents;

        public int Width => 64;
        public int Height => 36;
        public bool IsClosing => false;
        public PresentPath Path => path;
        public int SwapInterval { get; private set; }
        public long LastSwapNs { get; private set; }
        public int Held { get; private set; }

        public void SetSwapInterval(int interval) => SwapInterval = interval;

        public PresentResult Present(GpuTexture picture) => PresentBlack();

        public PresentResult PresentBlack()
        {
            var hold = ++_presents % 10 == 0;
            if (hold)
                Held++;
            if (hold && path == PresentPath.DxgiFlip)
                return new PresentResult(false, MonoClock.Instance.NowNs, PresentProblem.Held);
            var capped = path == PresentPath.DxgiFlip && _presents % 15 == 0;
            var now = MonoClock.Instance.NowNs;
            var k = (long)Math.Floor((now - _start) / Period) + (hold ? 5 : capped ? 3 : 1);
            var returns = _start + (long)Math.Round(k * Period);
            var spin = new SpinWait();
            while (MonoClock.Instance.NowNs < returns)
                spin.SpinOnce(sleep1Threshold: -1);
            LastSwapNs = MonoClock.Instance.NowNs;
            return PresentResult.Done(LastSwapNs);
        }

        public void Dispose()
        {
            // Holds nothing.
        }
    }

    /// <summary>Presents that take four periods while joined (GL), or a flip queue that is full and a waitable that waits out its
    /// cap (DXGI), are the node's own pace, not the barrier's: the output locks through them. Only the driver's own fact, its barrier
    /// wait timing out, makes it fall out with the stall's words.</summary>
    [Theory]
    [InlineData(PresentPath.GlWindow)]
    [InlineData(PresentPath.DxgiFlip)]
    public async Task APresentThatTakesLongIsNoBarrierStallAndOnlyTheBarriersOwnTimeoutIs(PresentPath path)
    {
        var (rig, driver) = Start(Screen("op_a"));
        await using var _rig = rig;
        await Until(rig, () => rig.Host.Board is not null && rig.Host.Locks().Count == 1, "the board was never seen");
        using var presenter = new HeldPresenter(path);
        await rig.Host.InvokeAsync(r =>
        {
            r.Attach("op_a", presenter, 60);
            return true;
        });

        var applied = await rig.Run("SYNC APPLY");
        Assert.True(applied.Ok, applied.Payload);
        Assert.True(rig.Host.Locks()["op_a"].Locked, applied.Payload);
        Assert.True(presenter.Held >= 10, $"{presenter.Held} held presents");   // 120 refreshes to confirm, every tenth held

        driver.BarrierWaitTimesOut = true;
        await Until(rig, () => rig.Host.Locks()["op_a"].State == LockState.FellOut, "op_a never fell out");
        var view = rig.Host.Locks()["op_a"];
        Assert.Equal(LockReasons.Stall, view.Reason);
        Assert.Contains("the barrier stalled", view.Words, StringComparison.Ordinal);
        Assert.Contains("leave op_a", driver.Calls);
        await rig.Host.InvokeAsync(r =>
        {
            r.Detach("op_a");
            return true;
        });
    }

    [Fact]
    public async Task UnderTheLockAWindowedPacersCountDrivesShowTime()
    {
        const int w = 64, h = 36;
        var probe = RenderHost.TryCreate(RenderHostOptions.Default, out var why) ?? throw new InvalidOperationException(why);
        var display = probe.Displays()[0].Key;
        await probe.DisposeAsync();
        var driver = new FakeFrameLockDriver();
        var project = HostRig.WithOutputs(HostRig.Screen("op_w", SourceRef.Solid("#336699"), w, h, OutputKind.Window, display));
        await using var rig = HostRig.Start(project, options: RenderHostOptions.Default with { FrameLock = () => driver, FrameLockAuto = false });
        await Until(rig, () => rig.Host.Health() is [{ Open: true }] && rig.Host.Board is not null, "the window never opened");
        Assert.Equal("clock", rig.Host.TimeSource);

        var applied = await rig.Run("SYNC APPLY");
        Assert.True(applied.Ok, applied.Payload);
        Assert.True(rig.Host.Locks()["op_w"].Locked, applied.Payload);
        await Until(rig, () => rig.Host.TimeSource == "count", "show time never came from the count");
        var anchor = rig.Host.LocalAnchor();
        Assert.NotNull(anchor);
        Assert.True(anchor.Value.PeriodPs > 1_000_000_000, $"{anchor.Value.PeriodPs} ps");
        Assert.Equal("count", State(rig).GetProperty("sync").GetProperty("lock").GetProperty("time").GetString());
        var present = State(rig).GetProperty("outputs")[0].GetProperty("present");
        Assert.Equal("gl-window", present.GetProperty("path").GetString());
        Assert.Equal("gl", present.GetProperty("mode").GetString());

        Assert.True((await rig.Run("SYNC LEAVE")).Ok);
        await Until(rig, () => rig.Host.TimeSource == "clock", "show time stayed on the count after leaving");
    }

    [Fact]
    public async Task ADriverThatRefusesTheJoinFallsOutWithItsWordsAndLeaveFreesEveryOutput()
    {
        var (rig, driver) = Start(Screen("op_a"));
        await using var _rig = rig;
        await Until(rig, () => rig.Host.Board is not null && rig.Host.Locks().Count == 1, "the board was never seen");
        driver.RefuseJoins = "the swap chain is busy";
        var applied = await rig.Run("SYNC APPLY");
        Assert.True(applied.Ok, applied.Payload);
        var view = rig.Host.Locks()["op_a"];
        Assert.Equal(LockState.FellOut, view.State);
        Assert.Equal(LockReasons.JoinRefused, view.Reason);
        Assert.Contains("the swap chain is busy", view.Words, StringComparison.Ordinal);

        driver.RefuseJoins = null;
        var left = await rig.Run("SYNC LEAVE");
        Assert.True(left.Ok, left.Payload);
        await Until(rig, () => rig.Host.Locks()["op_a"].State is LockState.FreeUnknown or LockState.FreeMeasured, "op_a never went free");
        Assert.False(rig.Host.Intent.Apply);
    }
}
