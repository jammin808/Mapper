using PlayMap.Core.Gpu;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>Frames on the fixture's GL backend for the RHI tests: each opens the frame's list, runs the body and submits, a pass the
/// body left open ended first, so one failing test never leaves the shared backend inside a frame. Frame numbers grow across every
/// test of the run.</summary>
internal static class BackendFrames
{
    private static long _frame;

    /// <summary>The frame number the next <see cref="Run"/> takes.</summary>
    public static long Next => Interlocked.Read(ref _frame) + 1;

    /// <summary>One frame: begin, the body, submit; answers the frame's value.</summary>
    public static FenceValue Run(GlBackend backend, Action<ICommandList> body)
    {
        var list = backend.BeginFrame(Interlocked.Increment(ref _frame));
        try
        {
            body(list);
        }
        finally
        {
            if (list is GlCommandList { InPass: true })
                list.EndPass();
        }
        return backend.Submit(list);
    }

    /// <summary>Empty frames until <paramref name="done"/> holds, at most <paramref name="frames"/> of them (the compile lane and
    /// warm-ups are taken as frames begin); true when it held.</summary>
    public static bool Until(GlBackend backend, Func<bool> done, int frames = 2000)
    {
        for (var i = 0; i < frames && !done(); i++)
        {
            Run(backend, _ => { });
            if (!done())
                Thread.Sleep(1);
        }
        return done();
    }

    /// <summary>A target's pixels read now (BGRA, top row first).</summary>
    public static byte[] Read(IGpuDevice device, RenderTarget target)
    {
        var bytes = new byte[target.Width * target.Height * 4];
        device.ReadNow(target, bytes);
        return bytes;
    }
}
