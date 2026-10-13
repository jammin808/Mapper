using System.Collections.Immutable;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Model;

/// <summary>What the edit overlay draws on the wall.</summary>
[Flags]
public enum OverlayLayers
{
    None = 0, Cursor = 1, Outlines = 2, Names = 4, Handles = 8, Lattice = 16, Pins = 32, Grid = 64,
    Edit = Cursor | Outlines | Names | Handles | Lattice, All = 127,
}

public enum HandleKind { Corner, Node }

/// <summary>A handle of a quad surface. Corner: Column 0..3 (TL, TR, BR, BL), Row 0.</summary>
public readonly record struct OverlayHandle(string SurfaceId, HandleKind Kind, int Column, int Row);

/// <summary>The edit overlay as the wall shows it: on which outputs, which layers, the selected handle, a grid flash, and the
/// lease that turns it off when the session that asked stops renewing (15 s). Runtime, never in the project file.</summary>
public sealed record OverlayState(bool On, ImmutableArray<string> Outputs, OverlayLayers Layers, OverlayHandle? Handle,
    long GridUntilLeaderNs, string Session, long LeaseUntilMonoNs)
{
    public static readonly OverlayState Off = new(false, [], OverlayLayers.Edit, null, 0, "", 0);
}

/// <summary>One session's cursor on the wall, in wall-normalised units of an output, with the clicks so far.</summary>
public sealed record WallCursor(string Session, string Name, string OutputId, Vec2d At, ImmutableArray<Vec2d> Trail, string Color,
    long ReceivedMonoNs, long ExpiresMonoNs);
