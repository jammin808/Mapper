using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Site;

/// <summary>A rigid move of the stage, x ↦ R·x + t (R a rotation, in double): what a change of the site frame does to everything
/// placed in stage coordinates, so every grid position stays where it is. Projectors, cameras and viewpoints keep their optics;
/// their poses, the pins' world points, the surfaces' placements, the projectors' position priors, the calibration ledger's poses
/// and the LED fixtures placed in the world move together, so every pin's reprojection is unchanged. Mesh files are never
/// rewritten: surfaces carry the move.</summary>
public readonly record struct StageMove(Mat3d R, Vec3d T)
{
    public static readonly StageMove None = new(Mat3d.Identity, Vec3d.Zero);

    /// <summary>The move taking stage coordinates in <paramref name="from"/> to the same grid points in <paramref name="to"/>.
    /// Both frames' grid scales must be equal (a change of scale is not a rigid move).</summary>
    public static StageMove Between(SiteFrame from, SiteFrame to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var t = to.ToStage(from.ToGrid(Vec3d.Zero));
        var r = Mat3d.FromColumns(to.ToStage(from.ToGrid(Vec3d.UnitX)) - t, to.ToStage(from.ToGrid(Vec3d.UnitY)) - t,
            to.ToStage(from.ToGrid(Vec3d.UnitZ)) - t);
        return new StageMove(r.NearestRotation(), t);
    }

    public bool IsNone => T == Vec3d.Zero && R == Mat3d.Identity;

    public Vec3d Apply(Vec3d p) => R * p + T;

    public Pose Apply(Pose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        return new Pose(Apply(pose.Position), (R * pose.CameraToWorld).NearestRotation().ToRotationVector());
    }

    public Transform3 Apply(Transform3 tr)
    {
        ArgumentNullException.ThrowIfNull(tr);
        return tr with { Position = Apply(tr.Position), Rotation = (R * Mat3d.FromRotationVector(tr.Rotation)).NearestRotation().ToRotationVector() };
    }

    private ImmutableArray<CalibrationPin> Apply(ImmutableArray<CalibrationPin> pins)
    {
        var self = this;
        return [.. pins.Select(p => p with { World = self.Apply(p.World) })];
    }

    /// <summary>The project with everything placed in stage coordinates moved: the stage, and the LED fixtures placed in the world
    /// (a fixture on a canvas, an output, a source or a surface is placed in that, which carries it).</summary>
    public Project Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (IsNone)
            return project;
        var self = this;
        var moved = project.WithStage(st => self.Apply(st));
        return project.Leds.Fixtures.Any(f => f.Space == LedSpace.World)
            ? moved with
            {
                Leds = moved.Leds with
                {
                    Fixtures = [.. moved.Leds.Fixtures.Select(f => f.Space == LedSpace.World ? f with { Placement = self.Apply(f.Placement) } : f)],
                },
            }
            : moved;
    }

    /// <summary>The stage with everything placed in it moved.</summary>
    public Stage Apply(Stage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (IsNone)
            return stage;
        var self = this;
        return stage with
        {
            Surfaces = [.. stage.Surfaces.Select(s => s.Pixel is not null ? s : s with { Transform = self.Apply(s.Transform) })],
            Projectors = [.. stage.Projectors.Select(pj => pj with
            {
                Pose = self.Apply(pj.Pose),
                Pins = self.Apply(pj.Pins),
                Priors = pj.Priors.Position is { } at ? pj.Priors with { Position = self.Apply(at) } : pj.Priors,
            })],
            Cameras = [.. stage.Cameras.Select(c => c with { Pose = self.Apply(c.Pose) })],
            Viewpoints = [.. stage.Viewpoints.Select(v => v with { Pose = self.Apply(v.Pose) })],
            Calibrations = [.. stage.Calibrations.Select(c => c.Projector is { } pc
                ? c with { Projector = pc with { Pose = self.Apply(pc.Pose), Pins = self.Apply(pc.Pins) } }
                : c)],
        };
    }
}

/// <summary>Pins and position priors bound to control points: a bound pin's world point, and a prior's position, is its control
/// point's grid point in the site frame, kept so by every edit that changes the site or the points (the edit that changes them
/// re-derives them in the same edit).</summary>
public static class ControlBinding
{
    /// <summary>The stage with every bound pin's world point re-derived from its control point; a pin whose point is gone keeps its
    /// last world point (and its binding, so SITE CHECK can name it).</summary>
    public static Stage Rebind(Stage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Controls.IsDefaultOrEmpty
            || !stage.Projectors.Any(pj => pj.Priors.ControlId.Length > 0 || pj.Pins.Any(p => p.ControlId.Length > 0)))
            return stage;
        var byId = stage.Controls.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var site = stage.Site;
        return stage with
        {
            Projectors = [.. stage.Projectors.Select(pj => pj.Priors.ControlId.Length > 0 || pj.Pins.Any(p => p.ControlId.Length > 0)
                ? pj with
                {
                    Pins = [.. pj.Pins.Select(p => p.ControlId.Length > 0 && byId.TryGetValue(p.ControlId, out var cp)
                        ? p with { World = site.ToStage(cp.Grid) }
                        : p)],
                    Priors = pj.Priors.ControlId.Length > 0 && byId.TryGetValue(pj.Priors.ControlId, out var at)
                        ? pj.Priors with { Position = site.ToStage(at.Grid) }
                        : pj.Priors,
                }
                : pj)],
        };
    }
}
