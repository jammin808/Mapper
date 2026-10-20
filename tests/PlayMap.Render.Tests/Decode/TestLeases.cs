using PlayMap.Core.Media;

namespace PlayMap.Render.Tests.Decode;

/// <summary>A lease a test builds: CPU planes, a GPU surface or an arena block, with the layout and generation it says.</summary>
internal sealed class TestLease(PixelLayout layout, int width, int height, long generation, FramePlane[]? planes = null,
    GpuSurface? surface = null, UploadBlock staged = default) : VideoFrameLease
{
    public override PixelLayout Layout => layout;
    public override int Width => width;
    public override int Height => height;
    public override IReadOnlyList<FramePlane> Planes => planes ?? [];
    public override long PtsNs => 0;
    public override long DurationNs => 40_000_000;
    public override long Generation => generation;
    public override ColorMatrix Matrix => ColorMatrix.Bt709;
    public override GpuSurface? Surface => surface;
    public override UploadBlock Staged => staged;

    protected override void Release()
    {
        // The test owns the memory the planes point at.
    }
}

/// <summary>A lease that carries a surface as a decoder's does: disposing it gives back the lease's reference to the surface.</summary>
internal sealed class SurfaceLease(PixelLayout layout, int width, int height, long generation, GpuSurface surface) : VideoFrameLease
{
    public override PixelLayout Layout => layout;
    public override int Width => width;
    public override int Height => height;
    public override IReadOnlyList<FramePlane> Planes => [];
    public override long PtsNs => 0;
    public override long DurationNs => 40_000_000;
    public override long Generation => generation;
    public override ColorMatrix Matrix => ColorMatrix.Bt709;
    public override GpuSurface? Surface => surface;

    protected override void Release() => surface.Release();
}

/// <summary>A decoder's slot as its pool counts references: the lease's (<see cref="Leased"/>) and every hold the renderer takes. When
/// the count reaches 0 the slot is the decoder's again, so it must not reach 0 while the GPU can still read the surface
/// (<paramref name="readDone"/>), nor be retained once it did; each such fault is recorded.</summary>
internal sealed class ReferenceOwner(Func<bool> readDone) : IGpuSurfaceOwner
{
    public int Count { get; private set; }
    public int Retains { get; private set; }
    public int Releases { get; private set; }
    public List<string> Faults { get; } = [];

    /// <summary>The lease's reference (the decoder's queue has already let go of its own).</summary>
    public void Leased() => Count++;

    public void Retain(GpuSurface surface)
    {
        if (Count == 0)
            Faults.Add("retained after the slot went back to the decoder");
        Count++;
        Retains++;
    }

    public void Release(GpuSurface surface)
    {
        Releases++;
        Count--;
        if (Count < 0)
            Faults.Add("released more often than it was referenced");
        else if (Count == 0 && !readDone())
            Faults.Add("given back to the decoder while the GPU could still read it");
    }
}

/// <summary>A producer's side of a surface's holds, counted.</summary>
internal sealed class CountingOwner : IGpuSurfaceOwner
{
    private int _holds;

    public int Holds => Volatile.Read(ref _holds);
    public int Peak { get; private set; }

    public void Retain(GpuSurface surface)
    {
        var n = Interlocked.Increment(ref _holds);
        Peak = Math.Max(Peak, n);
    }

    public void Release(GpuSurface surface) => Interlocked.Decrement(ref _holds);
}

/// <summary>A renderer share for tests: it records the import failures the texture set reports.</summary>
internal sealed class RecordingShare : IGpuShare
{
    private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);

    public GpuApi Api => GpuApi.DmaBuf;
    public string Describe => "test";
    public nint D3D11Device => 0;
    public long AdapterLuid => 0;
    public string? DrmRenderNode => null;
    public long DeviceEpoch => 1;
    public bool DirectProven => true;

    public bool CanImport(GpuSurfaceFormat format, uint fourcc, ulong modifier) => true;

    public void Enter()
    {
        // One render thread: nothing to enter.
    }

    public void Exit()
    {
        // As Enter.
    }

    public void ReportImportFailure(string sourceKey, string words) => _failures[sourceKey] = words;

    public bool ImportFailed(string sourceKey, out string words) => _failures.TryGetValue(sourceKey, out words!);
}
