using System.Collections.Immutable;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan.MultiView;

/// <summary>A projector of a multi-view scene: its id and its true optics and pose.</summary>
public sealed record SimProjector(string Id, SimDevice Device);

/// <summary>A place the phone stood: the view's id and the phone's true pose there.</summary>
public sealed record SimView(string Id, Pose Pose);

/// <summary>A scene several projectors light and one phone sees from several places: the mesh, its albedo and
/// colour, the projectors, the phone's true optics and the views (world +Y up, metres).</summary>
public sealed record MultiViewScene(
    string Name,
    MeshData Mesh,
    Func<Vec3d, double> Albedo,
    Func<Vec3d, int> Colour,
    ImmutableArray<SimProjector> Projectors,
    Intrinsics Phone,
    LensDistortion PhoneLens,
    ImmutableArray<SimView> Views)
{
    public SimProjector Projector(string id) => Projectors.Single(p => p.Id == id);

    public SimView View(string id) => Views.Single(v => v.Id == id);

    /// <summary>The single-projector, single-camera scene the scan simulator traces for one view of one
    /// projector: the same mesh, that projector, and the phone at that view's pose.</summary>
    public SimScene For(string viewId, string projectorId) => new(
        Name + " " + viewId + "/" + projectorId, Mesh, Albedo, Colour, Projector(projectorId).Device,
        new SimDevice(Phone, PhoneLens, View(viewId).Pose));
}

/// <summary>Ready-made multi-view scenes for the simulator. Resolutions are the caller's; the optics scale with them.</summary>
public static class MultiViewScenes
{
    /// <summary>The phone's horizontal field of view in degrees (a phone's main camera at 4:3).</summary>
    public const double PhoneFovDegrees = 64;

    /// <summary>The phone's residual lens distortion (a phone's processed picture is nearly rectilinear).</summary>
    public static readonly LensDistortion PhoneLens = new(0.03, -0.01, 0, 0, 0);

    /// <summary>The wall-and-cylinder scene of <see cref="SimScenes.WallAndCylinder"/> lit by two projectors that
    /// overlap on the cylinder (throw 1.2 from the left, 1.0 from the right) and seen by a phone from three places:
    /// left, right and low in the middle. From any one place the cylinder hides part of the wall and the phone's
    /// frame leaves part of the light out; the three together see nearly all of it.</summary>
    public static MultiViewScene WallAndCylinder(int projectorWidth = 512, int projectorHeight = 320, int cameraWidth = 480, int cameraHeight = 360)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(projectorWidth, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(projectorHeight, 16);
        var single = SimScenes.WallAndCylinder();
        SimProjector Projector(string id, double throwRatio, Vec3d eye, Vec3d target) => new(id,
            new SimDevice(Intrinsics.FromThrowRatio(projectorWidth, projectorHeight, throwRatio), LensDistortion.None, Pose.LookAt(eye, target, Vec3d.UnitY)));
        static SimView View(string id, Vec3d eye, Vec3d target) => new(id, Pose.LookAt(eye, target, Vec3d.UnitY));
        return new MultiViewScene(
            "wall and cylinder",
            single.Mesh,
            single.Albedo,
            single.Colour,
            [
                Projector("pj1", 1.2, new Vec3d(-0.6, 1.5, 3.2), new Vec3d(-0.15, 0.85, 0)),
                Projector("pj2", 1.0, new Vec3d(0.75, 1.6, 3.0), new Vec3d(0.35, 0.95, 0)),
            ],
            Intrinsics.FromFieldOfView(cameraWidth, cameraHeight, PhoneFovDegrees),
            PhoneLens,
            [
                View("v1", new Vec3d(-1.5, 1.3, 1.7), new Vec3d(-0.3, 0.8, 0)),
                View("v2", new Vec3d(1.6, 1.4, 1.8), new Vec3d(0.4, 0.8, 0)),
                View("v3", new Vec3d(0.15, 0.55, 2.0), new Vec3d(0.1, 0.95, 0)),
            ]);
    }
}

/// <summary>One simulated view of one projector: the decode, and what the camera truly saw.</summary>
public sealed record MultiViewCapture(string ViewId, string ProjectorId, CorrespondenceMap Map, SimTruth Truth);

/// <summary>
/// Multi-view scans with no hardware, over the existing <see cref="ScanSimulator"/>: each view of each projector is
/// its own single-projector scene (<see cref="MultiViewScene.For"/>), traced, rendered frame by frame and decoded as
/// a real scan of that projector from that place would be. Deterministic for the options' seed; the seed is varied
/// per capture so no two captures share their noise.
/// </summary>
public sealed class MultiViewSim
{
    private readonly ScanPlan _plan;
    private readonly SimOptions _sim;
    private readonly DecodeOptions _decode;
    private readonly MeshRaycaster _caster;

    public MultiViewSim(MultiViewScene scene, ScanPlanOptions? plan = null, SimOptions? sim = null, DecodeOptions? decode = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Projectors.IsDefaultOrEmpty)
            throw new ArgumentException("A scene needs a projector.", nameof(scene));
        var first = scene.Projectors[0].Device.Intrinsics;
        if (scene.Projectors.Any(p => p.Device.Intrinsics.Width != first.Width || p.Device.Intrinsics.Height != first.Height))
            throw new ArgumentException("The simulator scans every projector with one plan: their rasters must match.", nameof(scene));
        Scene = scene;
        _plan = ScanPlan.Create(first.Width, first.Height, plan ?? new ScanPlanOptions { Mode = ScanMode.Robust });
        _sim = sim ?? new SimOptions();
        _decode = decode ?? DecodeOptions.Default;
        _caster = new MeshRaycaster(scene.Mesh);
    }

    public MultiViewScene Scene { get; }

    public ScanPlan Plan => _plan;

    /// <summary>Scans one projector from one view: trace, render every plan frame, decode.</summary>
    public MultiViewCapture Capture(string viewId, string projectorId)
    {
        var vi = Scene.Views.IndexOf(Scene.View(viewId));
        var pi = Scene.Projectors.IndexOf(Scene.Projector(projectorId));
        var options = _sim with { Seed = _sim.Seed * 1000 + (ulong)(vi * 31 + pi) };
        var simulator = new ScanSimulator(new SimGeometry(Scene.For(viewId, projectorId)), _plan, options);
        var frames = simulator.RenderAll();
        var map = new StructuredLightDecoder(_plan, _decode).Decode(simulator.CameraWidth, simulator.CameraHeight, frames, simulator.RenderWhiteColour());
        return new MultiViewCapture(viewId, projectorId, map, simulator.Truth);
    }

    /// <summary>Every view of every projector, views outer, in the scene's order.</summary>
    public IReadOnlyList<MultiViewCapture> CaptureAll()
    {
        var list = new List<MultiViewCapture>();
        foreach (var v in Scene.Views)
            foreach (var p in Scene.Projectors)
                list.Add(Capture(v.Id, p.Id));
        return list;
    }

    /// <summary>The surface point a projector pixel truly lights (its ray's first hit), or null where the ray
    /// meets nothing.</summary>
    public Vec3d? TruthPoint(string projectorId, Vec2d projectorPixel)
    {
        var d = Scene.Projector(projectorId).Device;
        return _caster.Cast(CameraModel.PixelRay(projectorPixel, d.Intrinsics, d.Lens, d.Pose))?.Point;
    }

    /// <summary>The projector's raster in cells of <paramref name="cell"/> pixels (row-major), true where the
    /// ray through the cell's centre meets the scene: the lit raster coverage is measured against.</summary>
    public bool[] LitCells(string projectorId, int cell)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cell, 1);
        var k = Scene.Projector(projectorId).Device.Intrinsics;
        var cw = (k.Width + cell - 1) / cell;
        var ch = (k.Height + cell - 1) / cell;
        var lit = new bool[cw * ch];
        for (var y = 0; y < ch; y++)
        {
            for (var x = 0; x < cw; x++)
            {
                var centre = new Vec2d(Math.Min((x + 0.5) * cell, k.Width - 0.5), Math.Min((y + 0.5) * cell, k.Height - 0.5));
                lit[y * cw + x] = TruthPoint(projectorId, centre) is not null;
            }
        }
        return lit;
    }
}
