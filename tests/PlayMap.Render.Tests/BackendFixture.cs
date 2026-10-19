using System.Collections.Immutable;
using PlayMap.Core.Gpu;

namespace PlayMap.Render.Tests;

/// <summary>Every GPU backend this job has, one device each on a thread of its own, for every GPU test of the suite. The legs are
/// <see cref="BackendLegs.Makers"/>' in their order, the reference first: the backend the goldens and tolerances were drawn on (GL,
/// on llvmpipe here and on Mesa WGL on the Windows job). A scene is written against <see cref="IGpuDevice"/>, so it runs on any leg
/// (<see cref="On"/>); <see cref="Run{T}"/> runs it on the reference. The GL backend's own tests reach the GL leg through
/// <c>GlLegs</c>, and leave with it.</summary>
public sealed class BackendFixture : IDisposable
{
    public BackendFixture() => Legs = BackendLegs.Open();

    /// <summary>The legs this job has, the reference first.</summary>
    public ImmutableArray<BackendLeg> Legs { get; }

    /// <summary>The leg scenes run on unless they name one.</summary>
    public BackendLeg Reference => Legs[0];

    /// <summary>The leg of one backend; a job without it is refused with words.</summary>
    public BackendLeg On(GpuBackendKind kind)
    {
        foreach (var leg in Legs)
            if (leg.Kind == kind)
                return leg;
        throw new InvalidOperationException($"this job has no {kind} backend: its legs are {string.Join(", ", Legs.Select(l => l.Name))}");
    }

    /// <summary>Runs a scene on the reference leg's thread and returns its answer; an exception rethrows here with its stack.</summary>
    public T Run<T>(Func<BackendLeg, T> body) => Reference.Run(body);

    public void Run(Action<BackendLeg> body) => Reference.Run(body);

    public void Dispose()
    {
        for (var i = Legs.Length - 1; i >= 0; i--)
            Legs[i].Dispose();
    }
}

/// <summary>The GPU tests: one <see cref="BackendFixture"/> for them all.</summary>
[CollectionDefinition("gpu")]
public sealed class GpuCollection : ICollectionFixture<BackendFixture>;

/// <summary>Reading pictures back in tests.</summary>
public static class Pixels
{
    /// <summary>A target read back now as BGRA bytes, top row first.</summary>
    public static byte[] Read(IGpuDevice device, RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(target);
        var bytes = new byte[target.Width * target.Height * 4];
        device.ReadNow(target, bytes);
        return bytes;
    }

    /// <summary>A target's colour attachment read back now as RGBA floats, top row first: a fixed-point target's codes over
    /// 2^bits − 1, a float target's floats.</summary>
    public static float[] ReadFloat(IGpuDevice device, RenderTarget target, int attachment = 0)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(target);
        var floats = new float[target.Width * target.Height * 4];
        device.ReadNowFloat(target, attachment, floats);
        return floats;
    }

    /// <summary>(r, g, b) at a pixel of a BGRA picture.</summary>
    public static (int R, int G, int B) At(byte[] bgra, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return (bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    public static void Near((int R, int G, int B) expected, (int R, int G, int B) actual, int tolerance, string what = "")
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"{what} expected {expected} ±{tolerance}, got {actual}");
    }
}
