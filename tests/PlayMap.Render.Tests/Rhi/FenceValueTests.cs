using PlayMap.Core.Gpu;
using PlayMap.Core.Numerics;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>Fence values on the GL backend's timeline: each queue's values grow from 1 on their own; a value is complete once the GPU
/// passed it and stays complete, as does every value before it (<see cref="FenceRing.Completed"/> never falls); a ring of 64 signals
/// that wraps many times answers every value, old ones included, from the signals after it; off the ring's thread a value answers
/// from the owner's last poll and never polls; and on the fixture's GL context the same holds over real sync objects.</summary>
[Collection("gl")]
public sealed class FenceValueTests(GlFixture gl)
{
    /// <summary>Signals a test passes in order: a signal is its value's number, signalled once the test's GPU reaches it.</summary>
    private sealed class ScriptedGpu : IFenceSignals
    {
        private nint _next;
        public nint Reached { get; set; }
        public int Live { get; private set; }
        public int Polls { get; private set; }

        public nint Insert()
        {
            Live++;
            return ++_next;
        }

        public bool IsSignaled(nint signal)
        {
            Polls++;
            return signal <= Reached;
        }

        public void Delete(nint signal) => Live--;
    }

    [Fact]
    public void ValuesGrowPerQueueFromOne()
    {
        var gpu = new ScriptedGpu();
        using var direct = new FenceRing(QueueKind.Direct, gpu);
        using var copy = new FenceRing(QueueKind.Copy, gpu);
        Assert.Equal(new FenceValue(QueueKind.Direct, 1), direct.Signal());
        Assert.Equal(new FenceValue(QueueKind.Direct, 2), direct.Signal());
        Assert.Equal(new FenceValue(QueueKind.Copy, 1), copy.Signal());
        Assert.Equal(new FenceValue(QueueKind.Direct, 3), direct.Signal());
        Assert.Equal(new FenceValue(QueueKind.Copy, 2), copy.Signal());
        Assert.Equal((3ul, 2ul), (direct.Last, copy.Last));
        Assert.True(direct.IsComplete(FenceValue.None));
        Assert.True(copy.IsComplete(FenceValue.None));
        Assert.False(direct.IsComplete(new FenceValue(QueueKind.Direct, 4)));
        Assert.Throws<ArgumentException>(() => direct.IsComplete(new FenceValue(QueueKind.Copy, 1)));
    }

    [Fact]
    public void IsCompleteIsMonotonic()
    {
        var gpu = new ScriptedGpu();
        using var ring = new FenceRing(QueueKind.Direct, gpu);
        var random = new Pcg32(19);
        var answered = new bool[2001];
        for (var step = 0; step < 4000; step++)
        {
            if (ring.Last < 2000 && random.NextInt(3) != 0)
                ring.Signal();
            if (random.NextInt(4) == 0)
                gpu.Reached = Math.Min((nint)ring.Last, gpu.Reached + random.NextInt(6));
            var completedBefore = ring.Completed;
            var asked = (ulong)random.NextInt((int)ring.Last + 2);
            var complete = ring.IsComplete(new FenceValue(QueueKind.Direct, asked));
            Assert.True(ring.Completed >= completedBefore);
            Assert.Equal(asked <= ring.Completed, complete);
            Assert.True(ring.Completed <= (ulong)gpu.Reached, "a value answered complete before the GPU reached it");
            if (asked <= 2000)
            {
                Assert.False(answered[asked] && !complete, $"value {asked} was complete and is not");
                answered[asked] |= complete;
            }
            for (var v = 0ul; v <= ring.Completed && v <= 2000; v++)
                Assert.True(ring.IsComplete(new FenceValue(QueueKind.Direct, v)));
        }
    }

    [Fact]
    public void ARingOf64WrapsWithoutLosingAValue()
    {
        var gpu = new ScriptedGpu();
        using var ring = new FenceRing(QueueKind.Copy, gpu, 64);
        Assert.Equal(64, ring.Capacity);
        for (var i = 0; i < 300; i++)
            ring.Signal();
        Assert.Equal(64, gpu.Live);
        Assert.Equal(0ul, ring.Completed);
        for (var v = 1ul; v <= 300; v++)
            Assert.False(ring.IsComplete(new FenceValue(QueueKind.Copy, v)), $"value {v} complete before the GPU reached it");

        // Values 1–236 had their signals taken by later values; the GPU reaching 250 answers every one of them.
        gpu.Reached = 250;
        for (var v = 1ul; v <= 250; v++)
            Assert.True(ring.IsComplete(new FenceValue(QueueKind.Copy, v)), $"value {v} was lost when the ring wrapped");
        for (var v = 251ul; v <= 300; v++)
            Assert.False(ring.IsComplete(new FenceValue(QueueKind.Copy, v)));
        Assert.Equal(250ul, ring.Completed);
        Assert.Equal(50, gpu.Live);

        // Values 251–336 lose their signals to 315–400 while the GPU has not reached them: each is answered by the first later
        // value the ring still holds (337) once the GPU passes it, never before the GPU reached it.
        for (var i = 0; i < 100; i++)
            ring.Signal();
        Assert.Equal(64, gpu.Live);
        gpu.Reached = 290;
        Assert.False(ring.IsComplete(new FenceValue(QueueKind.Copy, 260)));
        Assert.True(ring.IsComplete(new FenceValue(QueueKind.Copy, 250)));
        gpu.Reached = 340;
        Assert.True(ring.IsComplete(new FenceValue(QueueKind.Copy, 260)));
        Assert.Equal(340ul, ring.Completed);
        Assert.False(ring.IsComplete(new FenceValue(QueueKind.Copy, 341)));
        gpu.Reached = 400;
        Assert.True(ring.IsComplete(new FenceValue(QueueKind.Copy, 400)));
        Assert.Equal(400ul, ring.Completed);
        Assert.Equal(0, gpu.Live);
    }

    /// <summary>Runs a call on a thread of its own and answers its result, or rethrows what it threw.</summary>
    private static T OffThread<T>(Func<T> call)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = call();
            }
            catch (InvalidOperationException ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join();
        return failure is null ? result : throw failure;
    }

    [Fact]
    public void OffTheRingsThreadAValueAnswersFromTheOwnersLastPollAndNeverPolls()
    {
        var gpu = new ScriptedGpu();
        using var ring = new FenceRing(QueueKind.Direct, gpu);
        var first = ring.Signal();
        _ = ring.Signal();
        gpu.Reached = 2;
        var polls = gpu.Polls;
        Assert.False(OffThread(() => ring.IsComplete(first)));
        Assert.Equal(polls, gpu.Polls);
        Assert.True(ring.IsComplete(new FenceValue(QueueKind.Direct, 2)));
        Assert.True(OffThread(() => ring.IsComplete(first)));
        Assert.Throws<InvalidOperationException>(() => OffThread(ring.Signal));
        Assert.Equal(2ul, ring.Last);
    }

    [Fact]
    public void OnGlSyncObjectsARingOf64WrapsAndEveryValueCompletes()
    {
        gl.Run(c =>
        {
            var device = c.Device;
            using var target = device.CreateRenderTarget(8, 8, TextureFormat.Rgba8);
            using var ring = new FenceRing(QueueKind.Direct, device, 64);
            var values = new List<FenceValue>();
            ulong seen = 0;
            for (var i = 0; i < 200; i++)
            {
                device.BindTarget(target);
                device.Clear(i / 200f, 0, 0, 1);
                values.Add(ring.Signal());
                foreach (var v in values)
                {
                    if (v.Value <= seen)
                        Assert.True(ring.IsComplete(v), $"value {v.Value} was complete and is not");
                }
                seen = ring.Completed;
            }
            Assert.Equal(Enumerable.Range(1, 200).Select(i => (ulong)i), values.Select(v => v.Value));
            device.Finish();
            foreach (var v in values)
                Assert.True(ring.IsComplete(v), $"value {v.Value} was lost when the ring wrapped");
            Assert.Equal(200ul, ring.Completed);
            Assert.Empty(device.TakeErrors());
        });
    }
}
