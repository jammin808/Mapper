using PlayMap.Core.Numerics;

namespace PlayMap.Core.Model;

/// <summary>A surface drawn in one projector's raster: four corners in raster-normalised units ((0,0) is the raster's top-left)
/// make a homography of the unit square; an optional lattice adds offsets on top of it (zero offsets are the plain quad). Lit by
/// that projector alone; a projector solve never moves it (a re-solve re-seats it by the measured displacement).</summary>
public sealed record PixelQuad(string ProjectorId, Vec2d TopLeft, Vec2d TopRight, Vec2d BottomRight, Vec2d BottomLeft, WarpLattice? Lattice);
