using System.Collections.Immutable;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Model;

public enum LedProtocol { ArtNet, Sacn, Ddp }
public enum LedSync { None, Latch }
public enum LedSpace { Canvas, Output, Source, Surface, World }
public enum LedShape { Line, Grid, Ring, Points }
public enum LedStart { TopLeft, TopRight, BottomLeft, BottomRight }
public enum LedSampling { Point, Area }
public enum LedChannel { R, G, B, W, WW, CW, A, UV, L, Dimmer, Constant, Gap }

/// <summary>A receiver of pixel data: an Art-Net node, an sACN receiver, a DDP/WLED controller.</summary>
/// <param name="Host">Address or name; "" = sACN multicast.</param><param name="Port">0 = the protocol's default.</param>
/// <param name="RateHz">0 = the protocol's default.</param><param name="LatencyMs">Node + network + driver, to land with the projection.</param>
/// <param name="UniverseChannels">Slots used per universe when auto-patching (default 510).</param>
public sealed record LedNode(string Id, string Name, LedProtocol Protocol, string Host, int Port, bool Enabled, double RateHz,
    double LatencyMs, LedSync Sync, int UniverseChannels, string Color) : IEntity
{
    public int SacnPriority { get; init; } = 100;
    public int SacnSyncUniverse { get; init; }
    public bool SacnMulticast { get; init; } = true;
    public bool FollowBlackout { get; init; } = true;
    public string Vendor { get; init; } = "";
}

/// <summary>One channel of a fixture type: what it carries, its bit depth, and its value when constant.</summary>
public sealed record LedChannelSlot(LedChannel Channel, int Bits, int Value);

/// <summary>A fixture type: the channels before, per and after its pixels. Built-in types ("builtin:grb", "builtin:rgbw"…) are
/// answered by the LED package and never stored.</summary>
public sealed record LedFixtureType(string Id, string Name, ImmutableArray<LedChannelSlot> Head, ImmutableArray<LedChannelSlot> PerPixel,
    ImmutableArray<LedChannelSlot> Tail, double WattsPerPixel, string Origin, string Manufacturer) : IEntity;

/// <summary>Where a fixture's pixels sit in its space: a line, grid or ring of a count, or explicit points.</summary>
public sealed record LedLayout(LedShape Shape, int Count, int Rows, Vec3d Size, LedStart Start, bool ColumnsFirst, bool Snake,
    ImmutableArray<Vec3d> Points);

/// <summary>Where a fixture's first channel lands.</summary>
/// <param name="Universe">Art-Net port-address 0–32767; sACN 1–63999; DDP 0.</param><param name="Address">1–512 (DDP: 1-based byte).</param>
public sealed record LedPatch(string NodeId, int Universe, int Address);

/// <summary>A fixture's brightness, white-balance gains and the gammas content and LEDs are read in.</summary>
public sealed record LedColor(double Brightness, double GainR, double GainG, double GainB, double GainW, double ContentGamma,
    double OutputGamma)
{
    public static readonly LedColor Neutral = new(1, 1, 1, 1, 1, 2.2, 2.2);
}

/// <summary>A run of pixels placed in a space, patched to a node, sampled from the picture.</summary>
public sealed record LedFixture(string Id, string Name, string TypeId, LedSpace Space, string SpaceId, Transform3 Placement,
    LedLayout Layout, LedPatch? Patch, LedSampling Sampling, double Footprint, LedColor Color, bool Enabled, bool Locked,
    bool Reverse, string EditorColor, ImmutableArray<string> Tags) : IEntity
{
    /// <summary>World fixtures sampled through a viewpoint.</summary>
    public string? ViewpointId { get; init; }
    /// <summary>Metres: the nearest-surface binding radius for world fixtures.</summary>
    public double BindDistance { get; init; } = 0.25;
}

/// <summary>The show's LED rig (wp2-led).</summary>
public sealed record LedRig
{
    public static readonly LedRig Empty = new();
    public ImmutableArray<LedNode> Nodes { get; init; } = [];
    public ImmutableArray<LedFixtureType> Types { get; init; } = [];
    public ImmutableArray<LedFixture> Fixtures { get; init; } = [];
}
