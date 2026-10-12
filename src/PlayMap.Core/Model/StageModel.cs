using System.Collections.Immutable;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Model;

/// <summary>A pinhole camera's intrinsics in pixels of the device's native raster (OpenCV convention:
/// x right, y down, the optical axis +z into the scene). A projector is a camera run backwards.</summary>
public sealed record Intrinsics(double Fx, double Fy, double Cx, double Cy, int Width, int Height)
{
    /// <summary>A projector from its datasheet: throw ratio (distance ÷ image width) and lens shift as a
    /// fraction of the image (+x right, +y up, the usual datasheet sign; 0 = centred on the axis). Shifting the
    /// image right puts the optical axis left of the image's centre (cx below w/2); shifting it up puts the axis
    /// below the centre (cy above h/2, pixel y pointing down).</summary>
    public static Intrinsics FromThrowRatio(int width, int height, double throwRatio, double shiftX = 0, double shiftY = 0)
    {
        var f = throwRatio * width;
        return new Intrinsics(f, f, width / 2.0 - shiftX * width, height / 2.0 + shiftY * height, width, height);
    }

    /// <summary>A camera from its horizontal field of view in degrees.</summary>
    public static Intrinsics FromFieldOfView(int width, int height, double horizontalFovDegrees)
    {
        var f = width / 2.0 / Math.Tan(horizontalFovDegrees * Math.PI / 360.0);
        return new Intrinsics(f, f, width / 2.0, height / 2.0, width, height);
    }

    public double ThrowRatio => Width > 0 ? Fx / Width : 0;

    public double HorizontalFovDegrees => 2 * Math.Atan(Width / (2 * Fx)) * 180 / Math.PI;

    public Mat3d K => new(Fx, 0, Cx, 0, Fy, Cy, 0, 0, 1);

    /// <summary>The same optics at another raster size (a camera's preview, a scaled output).</summary>
    public Intrinsics Scaled(int width, int height)
    {
        var sx = width / (double)Width;
        var sy = height / (double)Height;
        return new Intrinsics(Fx * sx, Fy * sy, Cx * sx, Cy * sy, width, height);
    }
}

/// <summary>Brown–Conrady lens distortion (radial k1 k2 k3, tangential p1 p2), OpenCV's order and meaning.</summary>
public sealed record LensDistortion(double K1, double K2, double P1, double P2, double K3)
{
    public static readonly LensDistortion None = new(0, 0, 0, 0, 0);

    public bool IsNone => K1 == 0 && K2 == 0 && P1 == 0 && P2 == 0 && K3 == 0;
}

/// <summary>Where a camera or projector sits: its optical centre in the world (metres, right-handed, +Y up)
/// and the rotation from its own frame (x right, y down, z forward) to the world's, as a rotation vector.</summary>
public sealed record Pose(Vec3d Position, Vec3d Rotation)
{
    public static readonly Pose Identity = new(Vec3d.Zero, Vec3d.Zero);

    /// <summary>Camera frame → world frame.</summary>
    public Mat3d CameraToWorld => Mat3d.FromRotationVector(Rotation);

    public Vec3d WorldToCamera(Vec3d world) => CameraToWorld.Transposed() * (world - Position);

    public Vec3d CameraToWorldPoint(Vec3d cam) => CameraToWorld * cam + Position;

    /// <summary>The unit direction the device looks along, in the world.</summary>
    public Vec3d Forward => CameraToWorld * Vec3d.UnitZ;

    /// <summary>A pose at <paramref name="eye"/> looking at <paramref name="target"/>, with the world's
    /// <paramref name="up"/> appearing up in the image (image y points down, so camera y = −up).</summary>
    public static Pose LookAt(Vec3d eye, Vec3d target, Vec3d up)
    {
        var z = (target - eye).Normalized();
        var x = Vec3d.Cross(z, up).Normalized();
        if (x.LengthSquared < 1e-12)
            x = Vec3d.Cross(z, Math.Abs(z.X) < 0.9 ? Vec3d.UnitX : Vec3d.UnitY).Normalized();
        var y = Vec3d.Cross(z, x);
        // Columns of camera→world are the camera axes in world coordinates.
        var r = Mat3d.FromColumns(x, y, z);
        return new Pose(eye, r.ToRotationVector());
    }
}

/// <summary>Position, rotation (rotation vector, radians) and scale of an object in the world.</summary>
public sealed record Transform3(Vec3d Position, Vec3d Rotation, Vec3d Scale)
{
    public static readonly Transform3 Identity = new(Vec3d.Zero, Vec3d.Zero, new Vec3d(1, 1, 1));

    public Vec3d Apply(Vec3d local)
    {
        var s = new Vec3d(local.X * Scale.X, local.Y * Scale.Y, local.Z * Scale.Z);
        return Mat3d.FromRotationVector(Rotation) * s + Position;
    }
}

public enum MeshOrigin
{
    Imported,
    Scanned,
    /// <summary>Made from parameters: plane, box, cylinder, sphere, dome.</summary>
    Primitive,
}

/// <summary>A mesh in the project. Vertex data lives in a file beside the project (or is generated for a
/// primitive) and is loaded into the mesh cache by content hash; the model holds only what it is.</summary>
public sealed record MeshAsset(
    string Id,
    string Name,
    MeshOrigin Origin,
    /// <summary>Relative to the project folder; empty for a primitive.</summary>
    string Path,
    string ContentHash,
    /// <summary>"plane", "box", "cylinder", "sphere", "dome" for a primitive.</summary>
    string Primitive,
    ParamSet PrimitiveParams,
    int Vertices,
    int Triangles) : IEntity;

public enum SurfaceMapping
{
    /// <summary>The source fills the mesh's own UVs (Region scales and offsets them, in UV units).</summary>
    Uv,
    /// <summary>The mesh's UVs sample a rectangle of a canvas (Region in canvas pixels).</summary>
    CanvasRegion,
    /// <summary>The source is projected onto the geometry from a viewpoint — design content on a photo of
    /// the building from the audience's side and it lands on every face.</summary>
    Projection,
}

/// <summary>What a surface shows and how it is laid on the geometry.</summary>
public sealed record SurfaceContent(SurfaceMapping Mode, SourceRef Source, RectD Region, string? ViewpointId)
{
    public static readonly SurfaceContent Empty = new(SurfaceMapping.Uv, SourceRef.None, new RectD(0, 0, 1, 1), null);
}

/// <summary>A mappable surface: a mesh (or a selection of its faces) placed in the world, with content and
/// soft masks. A scanned wall, one face of a box, a recognised object.</summary>
public sealed record Surface(
    string Id,
    string Name,
    string MeshId,
    Transform3 Transform,
    /// <summary>The triangles of the mesh this surface covers; empty = all of them.</summary>
    ImmutableArray<int> Faces,
    SurfaceContent Content,
    double Opacity,
    ImmutableArray<Mask> Masks,
    bool Visible,
    bool Locked,
    /// <summary>The colour the surface is drawn in the editor (#RRGGBB).</summary>
    string Color,
    /// <summary>Labels from recognition ("wall", "door", "column") and the operator.</summary>
    ImmutableArray<string> Tags) : IEntity;

/// <summary>A calibration pin: a point of the world (picked on a mesh) and the projector pixel that must
/// light it (dragged onto the real feature). Six or more spread, non-coplanar pins solve a projector.</summary>
public sealed record CalibrationPin(string Id, Vec3d World, Vec2d Pixel, bool Enabled) : IEntity;

/// <summary>What a pose solve may not change: measured lens shift and zoom are locked, as the field
/// advises, because spec-sheet values ruin solves and a solver that fits them chases noise.</summary>
[Flags]
public enum SolveLocks
{
    None = 0,
    /// <summary>Keep the principal point (lens shift).</summary>
    Shift = 1,
    /// <summary>Keep the focal length (zoom / throw ratio).</summary>
    Focal = 2,
    /// <summary>Keep the lens distortion.</summary>
    Distortion = 4,
}

/// <summary>A projector in the stage: optics and pose. Which output drives it is the output's to say.</summary>
public sealed record Projector(
    string Id,
    string Name,
    Intrinsics Intrinsics,
    LensDistortion Lens,
    Pose Pose,
    /// <summary>ANSI lumens (0 = unknown) — weights the automatic blend and the brightness preview.</summary>
    double Lumens,
    string Color,
    bool Locked,
    ImmutableArray<CalibrationPin> Pins,
    SolveLocks Locks,
    /// <summary>The last solve's reprojection RMS in pixels (0 = never solved).</summary>
    double SolveResidualPx) : IEntity;

public enum CameraSourceKind
{
    /// <summary>A webcam or capture device on this machine.</summary>
    Device,
    /// <summary>A phone that opened the node's /scan page.</summary>
    Phone,
    /// <summary>Photographs dropped in a folder, one per pattern (taken by hand from a written plan).</summary>
    Folder,
    Ndi,
    Stream,
}

/// <summary>A camera used to scan: where it is, how it sees, whether its optics are measured.</summary>
public sealed record CameraRig(
    string Id,
    string Name,
    CameraSourceKind SourceKind,
    string Source,
    Intrinsics Intrinsics,
    LensDistortion Lens,
    Pose Pose,
    bool Calibrated) : IEntity;

/// <summary>A viewpoint content can be projected from (<see cref="SurfaceMapping.Projection"/>).</summary>
public sealed record Viewpoint(string Id, string Name, Intrinsics Intrinsics, Pose Pose) : IEntity;

public enum ScanKind
{
    StructuredLight,
    Lidar,
    Photogrammetry,
    Import,
}

/// <summary>A scan kept with the project: the record here, the decoded maps, points and mesh in files.</summary>
public sealed record ScanRecord(
    string Id,
    string Name,
    ScanKind Kind,
    long CreatedUnixMs,
    string? CameraId,
    ImmutableArray<string> ProjectorIds,
    /// <summary>The scan's folder, relative to the project.</summary>
    string DataPath,
    string? MeshId,
    /// <summary>The worst projector's reprojection RMS in pixels (structured light) or the registration
    /// RMS in metres (LiDAR).</summary>
    double Residual,
    int Points,
    string Notes) : IEntity;

/// <summary>The 3D world: what is lit, what lights it, what looked at it.</summary>
public sealed record Stage(
    ImmutableArray<Surface> Surfaces,
    ImmutableArray<MeshAsset> Meshes,
    ImmutableArray<Projector> Projectors,
    ImmutableArray<CameraRig> Cameras,
    ImmutableArray<Viewpoint> Viewpoints,
    ImmutableArray<ScanRecord> Scans)
{
    public static readonly Stage Empty = new([], [], [], [], [], []);
}
