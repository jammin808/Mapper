using System.Collections.Immutable;
using PlayMap.Core.Content;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;

namespace PlayMap.Render.Engine;

/// <summary>Where the renderer gets decoded pictures: media, a playlist's running output, a live input, a
/// timer view. Called on the render thread once per source per frame; must never block (answer null to keep
/// the picture already on the GPU). The renderer uploads the lease and disposes it within the same frame.</summary>
public interface ISourceProvider
{
    /// <summary>The frame a source shows at a leader instant (the predicted vblank plus the output's
    /// latency, mapped to the leader's clock), or null to hold what the GPU has.</summary>
    VideoFrameLease? Acquire(SourceRef source, long leaderNs);

    /// <summary>What a playlist shows at a leader instant when that is a picture the renderer draws itself —
    /// a generator, a colour, a canvas, or <see cref="SourceRef.None"/> for black — or null when the playlist's
    /// pictures come as frames through <see cref="Acquire"/>. Called on the render thread (possibly more than
    /// once a frame); must not block. A provider that routes nothing keeps the default.</summary>
    SourceRef? Route(SourceRef source, long leaderNs) => null;

    /// <summary>The transition a playlist is in at a leader instant — the picture it leaves and the one it
    /// enters, how they mix and how far — or null when it shows one picture. Each side is a source the provider
    /// itself answers: a picture the renderer draws (a generator, a colour, a canvas, none) or a ref the
    /// provider made for one item's frames, which it answers through <see cref="Acquire"/> and never mixes or
    /// routes again. Called on the render thread; must not block. A provider that mixes nothing keeps the
    /// default.</summary>
    SourceMix? Mix(SourceRef source, long leaderNs) => null;

    /// <summary>The item a playlist shows at a leader instant (its current item, or the next once its switch came) and the leader
    /// instant that item started, or null — for a source that is not a playlist, an idle playlist, or a ref the provider made for
    /// one item's frames (a side of a mix, whose item <see cref="SourceMix"/> names). Called on the render thread; must not
    /// block. A provider that names no items keeps the default.</summary>
    ItemOnAir? Item(SourceRef source, long leaderNs) => null;
}

/// <summary>A playlist item on the air: its record (its effect chain) and the leader instant it started — the instant its in
/// point plays on its clip's anchor, the same on every node — from which its chain's time counts.</summary>
public readonly record struct ItemOnAir(PlaylistItem Item, long StartNs);

/// <summary>Two pictures of one playlist across a transition window: <see cref="From"/> going out,
/// <see cref="To"/> coming in, weighted by <see cref="Transitions.Weights"/> at <see cref="Progress"/> (0 = the
/// window opens, 1 = the switch) and mixed in linear light.</summary>
public readonly record struct SourceMix(SourceRef From, SourceRef To, TransitionKind Kind, double Progress)
{
    /// <summary>The item going out (<see cref="From"/>'s), or null when the provider names none.</summary>
    public ItemOnAir? FromItem { get; init; }

    /// <summary>The item coming in (<see cref="To"/>'s), or null when the provider names none.</summary>
    public ItemOnAir? ToItem { get; init; }
}

/// <summary>Outputs that show a picture raw this frame — a structured-light frame at the projector's native
/// pixels: the whole raster, with no warp, mask, blend, lens, trim, rotation, dither or identification card.
/// Checked after blackout (blackout still wins) and before freeze and the pattern override; every other
/// output keeps the show. Called on the render thread once per output per frame; must not block.</summary>
public interface IRawOutputs
{
    /// <summary>The source an output shows raw this frame, or null for the show.</summary>
    SourceRef? For(string outputId);
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

    /// <summary>When set, every output <see cref="PatternOutputs"/> names shows this source full-frame
    /// (through its warp, blend and trims) instead of its own content — "the grid on every projector".</summary>
    public SourceRef? PatternOverride { get; init; }

    /// <summary>The outputs <see cref="PatternOverride"/> covers; empty (the default) = every output.</summary>
    public ImmutableArray<string> PatternOutputs { get; init; }

    /// <summary>Outputs that show a picture raw this frame instead of anything else but blackout (null = none).</summary>
    public IRawOutputs? Raw { get; init; }

    /// <summary>Every output carries its identification card over the picture.</summary>
    public bool Identify { get; init; }

    /// <summary>The sting firing now (<see cref="EffectImpulse.None"/> for none).</summary>
    public EffectImpulse Impulse { get; init; }

    /// <summary>Per-output gain, safe-look override and hold (wp2-show). Null = gain 1, no override, no hold.</summary>
    public IAirLevels? Levels { get; init; }

    /// <summary>Modulation sources for bindings (wp2-audio's bus; wp2-fx's built-ins when audio is absent). Null = none.</summary>
    public PlayMap.Core.Modulation.IModulationBus? Modulation { get; init; }

    /// <summary>The analysed sound reactive content reads (wp2-audio). Null = none.</summary>
    public PlayMap.Core.Modulation.IAudioFrames? Audio { get; init; }

    /// <summary>The edit overlay (wp2-wall); off when <see cref="Armed"/>. Null = none.</summary>
    public OverlayState? Overlay { get; init; }

    /// <summary>Every session's cursor on the wall (wp2-wall); default = none.</summary>
    public ImmutableArray<WallCursor> Cursors { get; init; }

    /// <summary>The show is armed (the overlay stays off).</summary>
    public bool Armed { get; init; }

    /// <summary>Extra work at fixed points of the frame (effect chains, overlay, sends, LED sampling), in their Order; default = none.</summary>
    public ImmutableArray<IFramePass> Passes { get; init; }

    /// <summary>Each output's measured correction as the output stage draws it (wp2-align). Null = none.</summary>
    public IMeasuredStages? Measured { get; init; }

    public double LeaderSeconds => LeaderNs / 1e9;

    /// <summary>True when the pattern override is up and covers the output.</summary>
    public bool PatternCovers(string outputId) => PatternCovers(PatternOverride, PatternOutputs, outputId);

    /// <summary>True when a pattern is up and covers the output: <paramref name="outputs"/> names it, or is empty (every output).</summary>
    public static bool PatternCovers(SourceRef? pattern, ImmutableArray<string> outputs, string outputId)
    {
        if (pattern is not { IsNone: false })
            return false;
        if (outputs.IsDefaultOrEmpty)
            return true;
        foreach (var id in outputs)
            if (string.Equals(id, outputId, StringComparison.Ordinal))
                return true;
        return false;
    }
}
