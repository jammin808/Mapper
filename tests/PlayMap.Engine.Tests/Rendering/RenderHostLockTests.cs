using System.Reflection;
using PlayMap.Core.Model;
using PlayMap.Engine.Rendering;
using PlayMap.Render.Engine;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>Every lock the render thread takes beside other threads has its wait event before the first frame: a reader holding
/// one across a frame (on a loaded machine, a reader preempted while it holds it) makes the frame wait for it, and that wait, the
/// lock's first, allocates nothing on the frame path, at whatever frame it comes.</summary>
[Collection("render host")]
public sealed class RenderHostLockTests
{
    private static object Field(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
        ?? throw new MissingFieldException(owner.GetType().Name, name);

    /// <summary>Between two frames, on the render thread, the frame path's count is read (no reader beside it) and the test takes
    /// <paramref name="gate"/> before the next frame begins: that frame stops at it inside its counted span, the first wait the gate has
    /// had — seen once <paramref name="reached"/>, a count the frame moves on its way to the gate, has moved past the one read between
    /// the frames and the render thread waits. The gate is taken and let go on the test's thread, with no await between (a lock is its
    /// holder thread's). The frame path's count is the same five frames later.</summary>
    private static async Task AFrameWaitsBehindAReaderOf(HostRig rig, Lock gate, Func<RenderEngine, long> reached)
    {
        await rig.Host.FramesAsync(10).WaitAsync(HostRig.Patience);
        var (renderer, renderThread) = await rig.Host.InvokeAsync(r => (r, Thread.CurrentThread)).WaitAsync(HostRig.Patience);
        using var take = new ManualResetEventSlim();
        using var taken = new ManualResetEventSlim();
        Allocations.Begin();
        var bytes = -1L;
        var workFrame = -1L;
        var between = rig.Host.InvokeAsync(r =>
        {
            Volatile.Write(ref bytes, rig.Host.Stats.FramePathAllocatedBytes);
            Volatile.Write(ref workFrame, r.Frame);
            take.Set();
            return taken.Wait(HostRig.Patience);
        });
        Assert.True(take.Wait(HostRig.Patience), "the render thread ran no work between frames");
        bool waited;
        gate.Enter();
        try
        {
            taken.Set();
            // The next frame has come to the gate (its count moved) and its thread waits: on the gate, the one wait a solid output's
            // frame makes.
            waited = SpinWait.SpinUntil(() => between.IsCompleted && reached(renderer) > Volatile.Read(ref workFrame)
                && (renderThread.ThreadState & ThreadState.WaitSleepJoin) != 0, HostRig.Patience);
        }
        finally
        {
            gate.Exit();
        }
        Assert.True(await between, "the work between frames was never let go");
        Assert.True(waited, $"the frame after {Volatile.Read(ref workFrame)} never waited on the gate (at frame {renderer.Frame})");
        await rig.Host.FramesAsync(5).WaitAsync(HostRig.Patience);
        Assert.Equal(Volatile.Read(ref bytes), rig.Host.Stats.FramePathAllocatedBytes);
    }

    /// <summary>The gate a frame takes to record its present once it has drawn (the renderer's count moved), read by Stats and the
    /// present view from other threads.</summary>
    [Fact]
    public async Task AReaderHoldingTheFrameGateAcrossAFrameCostsTheFramePathNothing()
    {
        await using var rig = HostRig.Start(HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Solid("#336699"), 64, 36)));
        await AFrameWaitsBehindAReaderOf(rig, (Lock)Field(rig.Host, "_frameMsGate"), r => r.Frame);
    }

    /// <summary>The sting channel every frame reads its sting from as it composes, after its sources have begun it (their frame
    /// moved), written by the command lane as a sting is fired or cleared.</summary>
    [Fact]
    public async Task AWriterHoldingTheStingChannelAcrossAFrameCostsTheFramePathNothing()
    {
        await using var rig = HostRig.Start(HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Solid("#336699"), 64, 36)));
        var sources = Field(rig.Host, "_sources");
        var begun = sources.GetType().GetField("_frame", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(sources.GetType().Name, "_frame");
        await AFrameWaitsBehindAReaderOf(rig, (Lock)Field(rig.Engine.Content.Stings, "_gate"), _ => (long)begun.GetValue(sources)!);
    }

    [Fact]
    public async Task EveryLockTheRenderThreadTakesBesideOtherThreadsHasItsWaitEventBeforeItsFirstFrame()
    {
        // The probe reads a lock made bare: its first wait makes the event.
        Assert.True(LockWaitProbe.FirstWait(new Lock()) > 0, "a first wait on a bare lock makes its event");

        await using var rig = HostRig.Start(HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Solid("#336699"), 64, 36)));
        await rig.Host.FramesAsync(2).WaitAsync(HostRig.Patience);
        var sources = Field(rig.Host, "_sources");
        // The host's own gates (frame times, health, frame waiters, views, draft views), the frame lock's, the sources' busy gate,
        // their lease pool's and the engine's sting channel's.
        var owners = new[] { rig.Host, Field(rig.Host, "_lock"), sources, Field(sources, "_leases"), rig.Engine.Content.Stings };
        var probes = owners.SelectMany(o => LockWaitProbe.FirstWaits(o)).ToList();
        Assert.True(probes.Count >= 9, string.Join("; ", probes));
        Assert.All(probes, p => Assert.EndsWith(" 0 B", p, StringComparison.Ordinal));
    }
}
