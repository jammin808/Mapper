using PlayMap.Core.Gpu;

namespace PlayMap.Render.Tests;

/// <summary>Frames on a leg's device: each opens the frame's list, runs the body and submits, a pass the body left open ended first,
/// so one failing test never leaves the shared device inside a frame. Frame numbers grow across every test of the run.</summary>
internal static class BackendFrames
{
    private static long _frame;

    /// <summary>The frame number the next <see cref="Run(IGpuDevice, Action{ICommandList})"/> takes.</summary>
    public static long Next => Interlocked.Read(ref _frame) + 1;

    /// <summary>One frame: begin, the body, submit; answers the frame's value.</summary>
    public static FenceValue Run(IGpuDevice device, Action<ICommandList> body)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(body);
        var list = device.BeginFrame(Interlocked.Increment(ref _frame));
        try
        {
            body(list);
        }
        finally
        {
            if (BackendLegs.InPass(list))
                list.EndPass();
        }
        return device.Submit(list);
    }

    /// <summary>One frame whose body answers a value: begin, the body, submit.</summary>
    public static T Run<T>(IGpuDevice device, Func<ICommandList, T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = default(T)!;
        Run(device, list =>
        {
            result = body(list);
        });
        return result;
    }

    /// <summary>Empty frames until <paramref name="done"/> holds, at most <paramref name="frames"/> of them (the compile lane and
    /// warm-ups are taken as frames begin); true when it held.</summary>
    public static bool Until(IGpuDevice device, Func<bool> done, int frames = 2000)
    {
        ArgumentNullException.ThrowIfNull(done);
        for (var i = 0; i < frames && !done(); i++)
        {
            Run(device, _ => { });
            if (!done())
                Thread.Sleep(1);
        }
        return done();
    }

    /// <summary>One frame of one pass over <paramref name="target"/>: begin the frame, the pass, the body's draws, end the pass,
    /// submit.</summary>
    public static FenceValue Pass(IGpuDevice device, RenderTarget target, in PassDesc pass, Action<ICommandList> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var desc = pass;
        return Run(device, list =>
        {
            list.BeginPass(target, desc);
            body(list);
            list.EndPass();
        });
    }
}
