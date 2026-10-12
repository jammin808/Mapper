using PlayMap.Core.Content;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Media;
using PlayMap.Core.Model;

namespace PlayMap.Render.Engine;

/// <summary>Where the renderer gets decoded pictures: media, a playlist's running output, a live input, a
/// timer view. Called on the render thread once per source per frame; must never block (answer null to keep
/// the picture already on the GPU). The renderer uploads the lease and disposes it within the same frame.</summary>
public interface ISourceProvider
{
    /// <summary>The frame a source shows at a leader instant (the predicted vblank plus the output's
    /// latency, mapped to the leader's clock), or null to hold what the GPU has.</summary>
    VideoFrameLease? Acquire(SourceRef source, long leaderNs);
}

/// <summary>The geometry and maps the renderer consumes but does not make: meshes from the mesh cache, the
/// mapping module's warp meshes and blend maps, rasterised masks, lens lookups. Every object returned is
/// immutable once handed over — the renderer uploads it when a different instance arrives and keeps the GPU
/// copy while the same instance keeps coming back (identity, not contents, is the change test).</summary>
public interface IStageResources
{
    /// <summary>A mesh by id (null while it loads: surfaces on it are not drawn).</summary>
    MeshData? Mesh(string meshId);

    /// <summary>The output's tessellated 2D warp (null = identity).</summary>
    WarpMeshGeometry? Warp(string outputId);

    /// <summary>The output's blend map (null = weight one, no pedestal).</summary>
    BlendMap? Blend(string outputId);

    /// <summary>The output's mask coverage in normalised output space (null = no mask).</summary>
    CoverageMap? OutputMask(string outputId);

    /// <summary>A surface's mask coverage in its UV space (null = no mask).</summary>
    CoverageMap? SurfaceMask(string surfaceId);

    /// <summary>The output's lens pre-distortion lookup (null = no distortion).</summary>
    LensLookup? LensPredistortion(string outputId);
}

/// <summary>A coverage raster (0..1 per texel, row-major, top row first), upsampled bilinearly.</summary>
public sealed record CoverageMap(int Width, int Height, float[] Values)
{
    public float At(int x, int y) => Values[y * Width + x];
}

/// <summary>Lens pre-distortion: for each output pixel (row-major, top row first), where to sample the
/// undistorted picture, as normalised (u, v) pairs; a negative u marks a pixel the lens leaves dark.</summary>
public sealed record LensLookup(int Width, int Height, float[] SourceUv);

/// <summary>Resources that are all null: no meshes, identity warps, no blends, masks or lenses.</summary>
public sealed class EmptyStageResources : IStageResources
{
    public static readonly EmptyStageResources Instance = new();

    public MeshData? Mesh(string meshId) => null;
    public WarpMeshGeometry? Warp(string outputId) => null;
    public BlendMap? Blend(string outputId) => null;
    public CoverageMap? OutputMask(string outputId) => null;
    public CoverageMap? SurfaceMask(string surfaceId) => null;
    public LensLookup? LensPredistortion(string outputId) => null;
}

/// <summary>A provider with nothing to give (generators, canvases and colours still render).</summary>
public sealed class NoSources : ISourceProvider
{
    public static readonly NoSources Instance = new();

    public VideoFrameLease? Acquire(SourceRef source, long leaderNs) => null;
}

/// <summary>Everything one frame needs, handed to <see cref="RenderEngine.Render"/> once per frame. A value:
/// the engine fills it from its own state without allocating.</summary>
public readonly record struct FrameInputs
{
    /// <summary>The document snapshot this frame draws (immutable; never written again).</summary>
    public required Project Project { get; init; }

    /// <summary>The leader instant this frame is for, nanoseconds: the predicted vblank plus the display's
    /// latency on the leader's clock. Generators, sync marks and stings read it as seconds.</summary>
    public required long LeaderNs { get; init; }

    public required ISourceProvider Sources { get; init; }

    public required IStageResources Stage { get; init; }

    /// <summary>Every output black, before any content code runs.</summary>
    public bool Blackout { get; init; }

    /// <summary>Every output holds its last picture.</summary>
    public bool Freeze { get; init; }

    /// <summary>When set, every output shows this source full-frame (through its warp, blend and trims)
    /// instead of its own content — "the grid on every projector".</summary>
    public SourceRef? PatternOverride { get; init; }

    /// <summary>Every output carries its identification card over the picture.</summary>
    public bool Identify { get; init; }

    /// <summary>The sting firing now (<see cref="EffectImpulse.None"/> for none).</summary>
    public EffectImpulse Impulse { get; init; }

    public double LeaderSeconds => LeaderNs / 1e9;
}
