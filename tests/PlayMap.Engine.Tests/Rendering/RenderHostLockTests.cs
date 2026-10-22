using System.Reflection;
using PlayMap.Core.Model;
using PlayMap.Engine.Rendering;

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

    [Fact]
    public async Task AReaderHoldingTheFrameGateAcrossAFrameCostsTheFramePathNothing()
    {
        await using var rig = HostRig.Start(HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Solid("#336699"), 64, 36)));
        await rig.Host.FramesAsync(10).WaitAsync(HostRig.Patience);
        var (renderer, renderThread) = await rig.Host.InvokeAsync(r => (r, Thread.CurrentThread)).WaitAsync(HostRig.Patience);
        // The gate a frame takes to record its present, read by Stats and the present view from other threads.
        var gate = (Lock)Field(rig.Host, "_frameMsGate");
        using var take = new ManualResetEventSlim();
        using var taken = new ManualResetEventSlim();

        // Between two frames, on the render thread, the frame path's count is read (no reader beside it) and the test takes the gate
        // before the next frame begins: that frame stops at it inside its counted span, the first wait the gate has had. The gate is
        // taken and let go on this thread, with no await between (a lock is its holder thread's).
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
            // The next frame has begun (its count moved) and its thread waits: on the gate, the one wait a solid output's frame makes.
            waited = SpinWait.SpinUntil(() => between.IsCompleted && renderer.Frame > Volatile.Read(ref workFrame)
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

    [Fact]
    public async Task EveryLockTheRenderThreadTakesBesideOtherThreadsHasItsWaitEventBeforeItsFirstFrame()
    {
        // The probe reads a lock made bare: its first wait makes the event.
        Assert.True(LockWaitProbe.FirstWait(new Lock()) > 0, "a first wait on a bare lock makes its event");

        await using var rig = HostRig.Start(HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Solid("#336699"), 64, 36)));
        await rig.Host.FramesAsync(2).WaitAsync(HostRig.Patience);
        var sources = Field(rig.Host, "_sources");
        // The host's own gates (frame times, health, frame waiters, views, draft views), the frame lock's, the sources' busy gate and
        // their lease pool's.
        var owners = new[] { rig.Host, Field(rig.Host, "_lock"), sources, Field(sources, "_leases") };
        var probes = owners.SelectMany(o => LockWaitProbe.FirstWaits(o)).ToList();
        Assert.True(probes.Count >= 8, string.Join("; ", probes));
        Assert.All(probes, p => Assert.EndsWith(" 0 B", p, StringComparison.Ordinal));
    }
}
