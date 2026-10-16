using System.Collections.Immutable;
using PlayMap.Core.Model;

namespace PlayMap.Core.Led;

/// <summary>What a group of pixels samples: a canvas's composed picture, an output's final picture, or a source resolved at a size
/// (a surface's content, a source fixture); <see cref="None"/> samples black.</summary>
public enum LedBinding { None, Canvas, Output, Source }

/// <summary>Pixels that sample one picture the same way, contiguous in the plan's pixel order: one draw of the sample pass.</summary>
/// <param name="Key">The canvas or output id, or the source's wire form.</param>
/// <param name="Width">The size a source is resolved at (a canvas's or output's own size otherwise).</param>
/// <param name="Taps">1 (point: the texel under the pixel) or 9 (area: a 3 × 3 footprint, bilinear).</param>
/// <param name="Gamma">The content gamma the picture is decoded to linear light with.</param>
public sealed record LedGroup(LedBinding Binding, string Key, SourceRef Source, int Width, int Height, int First, int Count, int Taps, float Gamma);

/// <summary>One fixture as the plan compiled it: its index (the fixture texel row), its pixels (contiguous), the output whose air it
/// follows ("" = the rig's air, the darkest of the outputs), and the node it is patched to ("" = none).</summary>
public sealed record LedFixturePlan(string Id, string Name, int Index, int FirstPixel, int Pixels, string NodeId, string AirOutput,
    bool FollowBlackout, bool HasWhite, string Range);

/// <summary>One row of the patch: 512 slots of one node's universe (a DDP device's buffer is a run of rows, 512 bytes each).</summary>
public sealed record LedRow(string NodeId, int Universe, int Used);

/// <summary>Slots of a node's universe that more than one fixture claims (addresses 1-based, inclusive).</summary>
public sealed record LedConflict(string NodeId, int Universe, int From, int To, ImmutableArray<string> Fixtures);

/// <summary>A node as the plan sees it: its rows (first index and count), protocol, rate, its share of the bandwidth and whether the
/// plan can send to it.</summary>
public sealed record LedNodePlan(string NodeId, string Name, LedProtocol Protocol, int FirstRow, int Rows, double RateHz, double Mbps,
    byte DdpType, string Why);

/// <summary>The rig compiled for the render thread and the sender: immutable arrays, built off the render thread from one project.
/// Pixels are ordered by group, each fixture's contiguous; <see cref="Attributes"/> holds per pixel (u, v, rx, ry) — the sample
/// point in the picture's normalised coordinates (y down) and the area footprint's radii — with u &lt; 0 for a pixel that samples
/// black; <see cref="Patch"/> holds per slot (pixel, op, fixture, value) with op = channel × 4 + part (0: 8-bit, 1: 16-bit high
/// byte, 2: low byte) and op &lt; 0 for an unused slot; <see cref="Fixtures"/> holds four texels per fixture (gains R G B W;
/// brightness, output gamma, first pixel, pixels; editor colour R G B and content gamma; has-white, follows blackout, 0, 0).</summary>
public sealed class LedPlan
{
    public const int ColorsWidth = 1024;

    public const int SlotsPerRow = 512;

    public const int FixtureTexels = 4;

    public required long Serial { get; init; }

    public required int Pixels { get; init; }

    public required ImmutableArray<LedGroup> Groups { get; init; }

    public required float[] Attributes { get; init; }

    public required ImmutableArray<LedFixturePlan> Fixtures { get; init; }

    public required float[] FixtureTexelData { get; init; }

    public required ImmutableArray<LedRow> Rows { get; init; }

    public required float[] Patch { get; init; }

    public required ImmutableArray<LedNodePlan> Nodes { get; init; }

    public required ImmutableArray<LedConflict> Conflicts { get; init; }

    public required ImmutableArray<string> Warnings { get; init; }

    /// <summary>The estimated network load of every node sent to, Mb/s (UDP, IP and Ethernet headers included).</summary>
    public required double Mbps { get; init; }

    /// <summary>The bandwidth guard's refusal ("" = within it): when set, nothing is sent.</summary>
    public required string Refused { get; init; }

    /// <summary>The project the plan was built from (reference identity says whether it is current).</summary>
    public required LedRig Rig { get; init; }

    /// <summary>Rows of the colour target (1024 pixels a row).</summary>
    public int ColorRows => Math.Max(1, (Pixels + ColorsWidth - 1) / ColorsWidth);

    /// <summary>Distinct (node, universe) pairs sent (a DDP device counts its packets).</summary>
    public int Universes => Rows.Length;

    public static readonly LedPlan Empty = new()
    {
        Serial = 0,
        Pixels = 0,
        Groups = [],
        Attributes = [],
        Fixtures = [],
        FixtureTexelData = [],
        Rows = [],
        Patch = [],
        Nodes = [],
        Conflicts = [],
        Warnings = [],
        Mbps = 0,
        Refused = "",
        Rig = LedRig.Empty,
    };

    /// <summary>The row of a node's universe, or -1.</summary>
    public int RowOf(string nodeId, int universe)
    {
        for (var i = 0; i < Rows.Length; i++)
            if (Rows[i].Universe == universe && string.Equals(Rows[i].NodeId, nodeId, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>A node's plan entry, or null.</summary>
    public LedNodePlan? Node(string nodeId)
    {
        foreach (var n in Nodes)
            if (string.Equals(n.NodeId, nodeId, StringComparison.Ordinal))
                return n;
        return null;
    }

    /// <summary>A fixture's plan entry, or null.</summary>
    public LedFixturePlan? Fixture(string fixtureId)
    {
        foreach (var f in Fixtures)
            if (string.Equals(f.Id, fixtureId, StringComparison.Ordinal))
                return f;
        return null;
    }
}
