using System.Collections.Immutable;
using System.Runtime.InteropServices;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Tests;

/// <summary>Small projects and inputs for the GPU tests.</summary>
public static class Scene
{
    public static Generator Gen(string kind, ParamSet? ps = null, string id = "gn_test") => new(id, kind, kind, ps ?? ParamSet.Empty);

    public static Output SourceOutput(string id, SourceRef source, int w, int h, ColorTrim? trim = null, int rotation = 0) =>
        new(id, id, OutputKind.Virtual, "", w, h, 60, OutputMode.Source, null, source, new RectD(0, 0, w, h),
            OutputWarp.None, BlendSettings.Default with { Auto3D = false }, trim ?? ColorTrim.Neutral, [], new RectD(0, 0, w, h), rotation, true);

    public static Project Project(ImmutableArray<Generator> generators, ImmutableArray<Output> outputs, ImmutableArray<Canvas>? canvases = null, Stage? stage = null) =>
        new("pr_test", "Test", Core.Model.Project.CurrentSchema, [], [], generators, canvases ?? [], stage ?? Stage.Empty, outputs, ProjectSettings.Default);

    public static FrameInputs Inputs(Project project, double seconds = 1, ISourceProvider? sources = null, IStageResources? stage = null) => new()
    {
        Project = project,
        LeaderNs = (long)(seconds * 1e9),
        Sources = sources ?? NoSources.Instance,
        Stage = stage ?? EmptyStageResources.Instance,
    };

    /// <summary>A texture drawn into an 8-bit target and read back as BGRA, top row first.</summary>
    public static byte[] ReadTexture(GlDevice d, GpuTexture texture)
    {
        using var target = d.CreateRenderTarget(texture.Width, texture.Height, TextureFormat.Rgba8, filter: TextureFilter.Nearest);
        d.BindTarget(target);
        d.SetBlend(BlendMode.Opaque);
        var blit = d.Program("blit");
        blit.Set("uSrc", 0, 0, 1, 1);
        d.BindSampler(blit, "uTex", 0, texture);
        d.DrawFullscreen(blit);
        return Pixels.Read(d, target);
    }

    /// <summary>A generator rendered once at a size and read back.</summary>
    internal static byte[] RenderGenerator(GlDevice d, Generator g, int w, int h, GeneratorFrame frame, GeneratorContext? context = null)
    {
        using var services = new RenderServices(d);
        using var host = new GeneratorHost(services);
        host.BeginFrame(1);
        var tex = host.Render(g, w, h, context ?? new GeneratorContext("Output 1", 1, "#FF8800", true), frame);
        Assert.Empty(host.Faults());
        return ReadTexture(d, tex);
    }
}

/// <summary>Stage resources a test fills by hand.</summary>
public sealed class TestStage : IStageResources
{
    public Dictionary<string, MeshData> Meshes { get; } = [];
    public Dictionary<string, WarpMeshGeometry> Warps { get; } = [];
    public Dictionary<string, BlendMap> Blends { get; } = [];
    public Dictionary<string, CoverageMap> Masks { get; } = [];
    public Dictionary<string, CoverageMap> SurfaceMasks { get; } = [];
    public Dictionary<string, LensLookup> Lenses { get; } = [];

    public MeshData? Mesh(string meshId) => Meshes.GetValueOrDefault(meshId);
    public WarpMeshGeometry? Warp(string outputId) => Warps.GetValueOrDefault(outputId);
    public BlendMap? Blend(string outputId) => Blends.GetValueOrDefault(outputId);
    public CoverageMap? OutputMask(string outputId) => Masks.GetValueOrDefault(outputId);
    public CoverageMap? SurfaceMask(string surfaceId) => SurfaceMasks.GetValueOrDefault(surfaceId);
    public LensLookup? LensPredistortion(string outputId) => Lenses.GetValueOrDefault(outputId);
}

/// <summary>A decoded picture held in managed arrays, pinned for the lease's life.</summary>
public sealed class ArrayLease : VideoFrameLease
{
    private readonly GCHandle[] _pins;
    private readonly FramePlane[] _planes;

    public ArrayLease(PixelLayout layout, int width, int height, long generation, ColorMatrix matrix, ColorRange range, params (byte[] Data, int Stride, int Width, int Height)[] planes)
    {
        Layout = layout;
        Width = width;
        Height = height;
        Generation = generation;
        Matrix = matrix;
        Range = range;
        _pins = new GCHandle[planes.Length];
        _planes = new FramePlane[planes.Length];
        for (var i = 0; i < planes.Length; i++)
        {
            _pins[i] = GCHandle.Alloc(planes[i].Data, GCHandleType.Pinned);
            _planes[i] = new FramePlane(_pins[i].AddrOfPinnedObject(), planes[i].Stride, planes[i].Width, planes[i].Height);
        }
    }

    public override PixelLayout Layout { get; }
    public override int Width { get; }
    public override int Height { get; }
    public override IReadOnlyList<FramePlane> Planes => _planes;
    public override long PtsNs => 0;
    public override long DurationNs => 16_666_667;
    public override long Generation { get; }
    public override ColorMatrix Matrix { get; }
    public override ColorRange Range { get; }

    public TransferFunction TransferOverride { get; init; } = TransferFunction.Sdr;

    public override TransferFunction Transfer => TransferOverride;

    public int Releases { get; private set; }

    protected override void Release()
    {
        foreach (var p in _pins)
            p.Free();
        Releases++;
    }
}

/// <summary>A provider that hands out a lease built per call by a factory.</summary>
public sealed class FuncSources(Func<SourceRef, long, VideoFrameLease?> acquire) : ISourceProvider
{
    public int Calls { get; private set; }

    public VideoFrameLease? Acquire(SourceRef source, long leaderNs)
    {
        Calls++;
        return acquire(source, leaderNs);
    }
}
