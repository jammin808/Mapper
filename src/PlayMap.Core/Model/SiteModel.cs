using System.Collections.Immutable;
using System.Text.Json.Serialization;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Model;

/// <summary>The project's site frame: the stage's origin in a surveyed coordinate system, held in double so a facade 500 km
/// from the grid's origin is millimetre-exact after rebasing.</summary>
/// <param name="Crs">"EPSG:25832" | "LOCAL" | "" (not set).</param><param name="Origin">Grid E, N, H in metres.</param>
/// <param name="RotationDeg">Stage +X from grid east, anticlockwise seen from above.</param>
/// <param name="GridScale">Projection × elevation scale at the origin; 1 = unknown.</param><param name="MainsHz">50 | 60 | 0.</param>
public sealed record SiteFrame(string Crs, string CrsName, Vec3d Origin, double RotationDeg, double GridScale, int MainsHz)
{
    public static readonly SiteFrame None = new("", "", Vec3d.Zero, 0, 1, 0);
    public bool IsSet => Crs.Length > 0;

    /// <summary>Grid (E, N, H) to stage (+X east turned by the rotation, +Y up, +Z south), horizontal distances divided by the
    /// grid scale; in double throughout.</summary>
    public Vec3d ToStage(Vec3d grid)
    {
        var a = RotationDeg * Math.PI / 180;
        var s = GridScale > 0 ? GridScale : 1;
        double de = grid.X - Origin.X, dn = grid.Y - Origin.Y;
        var e = (Math.Cos(a) * de + Math.Sin(a) * dn) / s;
        var n = (-Math.Sin(a) * de + Math.Cos(a) * dn) / s;
        return new Vec3d(e, grid.Z - Origin.Z, -n);
    }

    /// <summary>Stage to grid: the inverse of <see cref="ToStage"/>.</summary>
    public Vec3d ToGrid(Vec3d stage)
    {
        var a = RotationDeg * Math.PI / 180;
        var s = GridScale > 0 ? GridScale : 1;
        double e = stage.X * s, n = -stage.Z * s;
        return new Vec3d(Origin.X + Math.Cos(a) * e - Math.Sin(a) * n, Origin.Y + Math.Sin(a) * e + Math.Cos(a) * n, Origin.Z + stage.Y);
    }
}

/// <summary>A surveyed point with its uncertainty and the surveyor's code.</summary>
public sealed record ControlPoint(string Id, string Name, Vec3d Grid, double SigmaM, string Code, bool Enabled) : IEntity;

public enum SurveyFormat { Obj, Ply, Gltf, Las, Xyz, Pts, Csv, E57 }
public enum SurveyState { Importing, Ready, Failed }

/// <summary>An imported survey (model or point cloud) with its provenance.</summary>
public sealed record SurveySource(string Id, string Name, SurveyFormat Format, string SourcePath, string SourceHash, string Crs,
    long SourcePoints, long SourceTriangles, string? MeshId, string CloudPath, double VoxelM, double DecimationErrorM,
    SurveyState State, long ImportedUnixMs, string Notes) : IEntity;

public enum StackFailover { Hold, Match }

/// <summary>Projectors lighting one picture: the blend treats them as one; each member's measured residual and gains live
/// on its output's MeasuredCorrection.</summary>
public sealed record StackGroup(string Id, string Name, ImmutableArray<string> ProjectorIds, string MasterId, StackFailover Failover) : IEntity
{
    /// <summary>The members as read: none when the file or a generic ADD gave no list.</summary>
    [JsonIgnore]
    public ImmutableArray<string> Members => ProjectorIds.IsDefault ? [] : ProjectorIds;
}

/// <summary>What a solve may assume: a throw ratio with its uncertainty, a position (a control point or coordinates). The file
/// names the ratio "throw" ("throwRatio" is a lens's derived value, never stored).</summary>
public sealed record SolvePriors([property: JsonPropertyName("throw")] double ThrowRatio,
    [property: JsonPropertyName("throwSigmaPct")] double ThrowRatioSigmaPct, Vec3d? Position, string ControlId, double PositionSigmaM,
    string Source)
{
    public static readonly SolvePriors None = new(0, 0, null, "", 0, "");
}

/// <summary>A solve's uncertainty from its own information matrix: on the wall (px), along the axis (m), the focal (%), and the
/// weak direction in words ("zoom↔distance").</summary>
public sealed record SolveUncertainty(double WallPx, double ProudPx, double FocalPct, double AxisM, double LateralM, double Condition,
    string Weak);
