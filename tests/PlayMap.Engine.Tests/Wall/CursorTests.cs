using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Tests;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Wall;

/// <summary>The wall cursor on a headless render loop: a cursor its session stops renewing for 3 s is gone from the next frame; two
/// sessions moving their cursors 300 times are drawn at most two frames late at p95 (frames of the render loop, not wall time), the
/// number in STATE.air.overlay.cursorP95Ms; and the frame path allocates nothing while they move.</summary>
[Collection("render host")]
public sealed class CursorTests(ITestOutputHelper log)
{
    private const int W = 640;
    private const int H = 360;

    private static readonly SessionInfo Alice = new("s-alice", "desk", "127.0.0.1", true, "Alice", true);
    private static readonly SessionInfo Bob = new("s-bob", "desk", "127.0.0.1", true, "Bob", true);

    private static Project OneProjector()
    {
        var p = Project.CreateDefault("Cursor test");
        var pj = new Projector("pj_1", "Projector 1", Intrinsics.FromThrowRatio(W, H, 1.5), LensDistortion.None,
            Pose.LookAt(new Vec3d(0, 1.5, 4), new Vec3d(0, 1.5, 0), Vec3d.UnitY), 0, "#FF8800", false, [], SolveLocks.None, 0);
        var o = new Output("op_1", "Output 1", OutputKind.Virtual, "", W, H, 60, OutputMode.Projector, "pj_1", SourceRef.None, new RectD(0, 0, W, H),
            OutputWarp.None, BlendSettings.Default with { Auto3D = false }, ColorTrim.Neutral, [], new RectD(0, 0, W, H), 0, true);
        return p with { Outputs = [o], Stage = p.Stage with { Projectors = [pj] } };
    }

    private static async Task Ok(HostRig rig, string line, SessionInfo session)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        var r = await rig.Engine.HandleAsync(parsed, session, CancellationToken.None);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
    }

    /// <summary>How many pixels of a cursor's colour sit within 30 px of a point.</summary>
    private static int Lit(byte[] px, double x, double y, (int R, int G, int B) colour)
    {
        var n = 0;
        for (var yy = (int)y - 30; yy <= (int)y + 30; yy++)
            for (var xx = (int)x - 30; xx <= (int)x + 30; xx++)
                if (xx >= 0 && yy >= 0 && xx < W && yy < H && HostRig.At(px, W, xx, yy) == colour)
                    n++;
        return n;
    }

    [Fact]
    public async Task ACursorNotRenewedForThreeSecondsIsGoneFromTheNextFrame()
    {
        await using var rig = HostRig.Start(OneProjector());
        await Ok(rig, "OVERLAY ON ON op_1", Alice);
        await Ok(rig, "CURSOR op_1 0.5 0.5 COLOR #FF00FF", Alice);
        var px = await rig.Read("op_1");
        Assert.True(Lit(px, W / 2.0, H / 2.0, (255, 0, 255)) > 20, "the cursor is not drawn");

        // 2.9 s without a renewal: still up. Past 3 s: the lane's tick takes it down, and the next frame has none.
        rig.Clock.AdvanceMs(2900);
        await rig.Engine.Lane.RunAsync(() => { });
        Assert.Single(rig.Engine.WallCursors);
        rig.Clock.AdvanceMs(150);
        await WallRig.Until(() => rig.Engine.WallCursors.IsEmpty, "the lease to lapse");
        px = await rig.Read("op_1");
        Assert.Equal(0, Lit(px, W / 2.0, H / 2.0, (255, 0, 255)));
        Assert.Equal(0, WallRig.Json(rig.Engine.State().Json).GetProperty("air").GetProperty("overlay").GetProperty("cursors").GetInt32());
    }

    [Fact]
    public async Task TwoMovingCursorsAreDrawnWithinTwoFramesAndTheFramePathAllocatesNothing()
    {
        await using var rig = HostRig.Start(OneProjector());
        await Ok(rig, "OVERLAY ON ON op_1", Alice);
        // Warm: both cursors up and drawn once, the overlay's buffers made.
        await Ok(rig, "CURSOR op_1 0.25 0.5 COLOR #FF00FF", Alice);
        await Ok(rig, "CURSOR op_1 0.75 0.5 COLOR #00FFFF", Bob);
        await rig.Host.FramesAsync(10).WaitAsync(HostRig.Patience);
        Allocations.Begin();
        var before = rig.Host.Stats;

        // Moves faster than frames: three lines between frame waits, the two sessions in turn.
        var rng = new Pcg32(42);
        for (var i = 0; i < 300; i++)
        {
            var who = i % 2 == 0 ? Alice : Bob;
            var x = (i % 2 == 0 ? 0.25 : 0.75) + rng.Range(-0.1, 0.1);
            var y = 0.5 + rng.Range(-0.2, 0.2);
            await Ok(rig, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"CURSOR op_1 {x:0.0000} {y:0.0000}"), who);
            if (i % 3 == 2)
                await rig.Host.FramesAsync(1).WaitAsync(HostRig.Patience);
        }
        await rig.Host.FramesAsync(2).WaitAsync(HostRig.Patience);
        var after = rig.Host.Stats;

        var p95 = await PollP95(rig);
        var period = 1000.0 / 60;
        log.WriteLine($"cursor p95 {p95:0.0} ms ({p95 / period:0.0} frames) over 300 moves; {after.Frames - before.Frames} frames, " +
            $"{after.FramePathAllocatedBytes - before.FramePathAllocatedBytes} bytes on the frame path");
        Assert.True(p95 <= 2 * period + 0.1, $"{p95} ms");
        Assert.Equal(before.FramePathAllocatedBytes, after.FramePathAllocatedBytes);
    }

    [Fact]
    public async Task AMoveTheOverlayDoesNotDrawIsMeasuredWhenItIsDrawnAndReadsLate()
    {
        await using var rig = HostRig.Start(OneProjector());
        // The overlay on without its cursor layer: the move is on the board but no frame draws it.
        await Ok(rig, "OVERLAY ON ON op_1 LAYERS outlines", Alice);
        await Ok(rig, "CURSOR op_1 0.5 0.5 COLOR #FF00FF", Alice);
        await rig.Host.FramesAsync(12).WaitAsync(HostRig.Patience);
        Assert.Equal(-1, WallRig.Json(rig.Engine.State().Json).GetProperty("air").GetProperty("overlay").GetProperty("cursorP95Ms").GetDouble());
        // The layer on: the first frame that draws it is a dozen frames after the line arrived, and the measure says so.
        await Ok(rig, "OVERLAY ON ON op_1 LAYERS ALL", Alice);
        var p95 = await PollP95(rig);
        log.WriteLine($"a move drawn late reads {p95:0.0} ms ({p95 / (1000.0 / 60):0.0} frames)");
        Assert.True(p95 > 10 * 1000.0 / 60, $"{p95} ms");
    }

    /// <summary>STATE's cursor p95 once the render host has reported one.</summary>
    private static async Task<double> PollP95(HostRig rig)
    {
        double value = -1;
        await WallRig.Until(() =>
        {
            value = WallRig.Json(rig.Engine.State().Json).GetProperty("air").GetProperty("overlay").GetProperty("cursorP95Ms").GetDouble();
            return value > 0;
        }, "the cursor latency in STATE");
        return value;
    }
}
