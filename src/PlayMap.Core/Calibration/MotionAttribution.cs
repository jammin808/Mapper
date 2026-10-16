using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Calibration;

/// <summary>What a dot check says about a projector: it stands where the project says (unmoved), it moved, the camera saw too few
/// of its dots (unseen), or every projector moved together (the camera moved, not they).</summary>
public enum MotionVerdict { Unmoved, Moved, Unseen, Camera }

/// <summary>One projector's part of a dot check: its dots (projector px), where the project says each lands in the camera (null: off
/// the stage or out of view), the spots seen, and which spot each dot matched.</summary>
public sealed record ProjectorDots(string ProjectorId, string Name, ImmutableArray<Vec2d> Dots, ImmutableArray<Vec2d?> Predicted,
    ImmutableArray<DotBlob> Observed, DotMatch Match);

/// <summary>A projector's verdict: how many dots were expected in view and matched, how far the matched ones landed from where the
/// project says (camera px, median and p95), and the words.</summary>
public sealed record ProjectorMotion(string ProjectorId, MotionVerdict Verdict, int Expected, int Matched, double ShiftP50, double ShiftP95, string Words);

/// <summary>
/// Who moved, from a dot check against the project (calibops §3.6, the minimal form): a projector with fewer than
/// <see cref="MinDots"/> matched dots is unseen; one whose matched dots land a median of more than <see cref="Threshold"/> camera px
/// from where its stored pose, the stage and the camera say is moved; when two or more are seen and every one moved, the camera moved
/// and no projector is re-solved (a camera re-solve is next round). The geometry helpers take a projector pixel to the camera through
/// the stage, and a camera pixel back onto the stage.
/// </summary>
public static class MotionAttribution
{
    public const int MinDots = 60;

    /// <summary>The median shift (camera px) above which a projector moved.</summary>
    public const double Threshold = 0.6;

    /// <summary>Where a projector pixel lands in the camera: its ray onto the stage, then into the camera, which must see that very
    /// point (its own ray hits the stage there first); null otherwise.</summary>
    public static Vec2d? Predict(Vec2d projectorPixel, DeviceView projector, DeviceView camera, MeshRaycaster stage)
    {
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Cast(projector.Ray(projectorPixel)) is not { } hit || camera.Project(hit.Point) is not { } c || !CameraModel.InRaster(c, camera.Intrinsics))
            return null;
        var back = camera.Ray(c);
        return stage.Cast(back) is { } seen && Vec3d.Distance(seen.Point, hit.Point) < 1e-3 * Math.Max(1, hit.Distance) ? c : null;
    }

    /// <summary>Where a camera pixel's ray meets the stage, or null.</summary>
    public static Vec3d? OnStage(Vec2d cameraPixel, DeviceView camera, MeshRaycaster stage)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Cast(camera.Ray(cameraPixel))?.Point;
    }

    /// <summary>The verdict for each projector of a check.</summary>
    public static List<ProjectorMotion> Attribute(IReadOnlyList<ProjectorDots> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var result = new List<ProjectorMotion>(check.Count);
        foreach (var c in check)
        {
            var expected = c.Predicted.Count(p => p is not null);
            var shifts = new List<double>();
            for (var i = 0; i < c.Dots.Length; i++)
                if (c.Match.Observed[i] >= 0 && c.Predicted[i] is { } p)
                    shifts.Add(Vec2d.Distance(c.Observed[c.Match.Observed[i]].At, p));
            shifts.Sort();
            var p50 = shifts.Count == 0 ? -1 : shifts[shifts.Count / 2];
            var p95 = shifts.Count == 0 ? -1 : shifts[Math.Min(shifts.Count - 1, (int)Math.Ceiling(0.95 * shifts.Count) - 1)];
            var matched = shifts.Count;
            var (verdict, words) = matched < MinDots
                ? (MotionVerdict.Unseen, $"{c.Name}: the camera matched {matched} of {expected} dots — is it on, its shutter open, in the camera's view?")
                : p50 > Threshold
                    ? (MotionVerdict.Moved, string.Create(CultureInfo.InvariantCulture, $"{c.Name} moved: its dots land {p50:0.0} px (p95 {p95:0.0}) from where the project says"))
                    : (MotionVerdict.Unmoved, string.Create(CultureInfo.InvariantCulture, $"{c.Name} has not moved ({p50:0.00} px)"));
            result.Add(new ProjectorMotion(c.ProjectorId, verdict, expected, matched, p50, p95, words));
        }
        var seen = result.Where(r => r.Verdict != MotionVerdict.Unseen).ToList();
        if (seen.Count >= 2 && seen.All(r => r.Verdict == MotionVerdict.Moved))
        {
            for (var i = 0; i < result.Count; i++)
                if (result[i].Verdict == MotionVerdict.Moved)
                    result[i] = result[i] with
                    {
                        Verdict = MotionVerdict.Camera,
                        Words = string.Create(CultureInfo.InvariantCulture, $"every projector's dots moved together — the camera moved, not {result[i].ProjectorId}; place the camera again (a scan from it: SCAN START <camera>) before re-solving projectors"),
                    };
        }
        return result;
    }

    /// <summary>The pairs a moved projector is re-solved from: each matched dot's spot taken onto the stage through the camera,
    /// paired with the dot's projector pixel (in the projector's intrinsic raster).</summary>
    public static List<Correspondence> Pairs(ProjectorDots dots, DeviceView camera, MeshRaycaster stage)
    {
        ArgumentNullException.ThrowIfNull(dots);
        var pairs = new List<Correspondence>();
        for (var i = 0; i < dots.Dots.Length; i++)
        {
            var j = dots.Match.Observed[i];
            if (j < 0)
                continue;
            if (OnStage(dots.Observed[j].At, camera, stage) is { } x)
                pairs.Add(new Correspondence(x, dots.Dots[i]));
        }
        return pairs;
    }

    /// <summary>The wall error of a pose against a fresh look at its dots (projector px): each matched spot taken onto the stage and
    /// projected through the pose, against the dot it is. Answers p50 and p95 (−1 with no pair).</summary>
    public static (double P50, double P95, int Count) WallError(IReadOnlyList<Correspondence> pairs, DeviceView projector)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(projector);
        var e = new List<double>(pairs.Count);
        foreach (var pair in pairs)
            if (projector.Project(pair.World) is { } px)
                e.Add(Vec2d.Distance(px, pair.Pixel));
        if (e.Count == 0)
            return (-1, -1, 0);
        e.Sort();
        return (e[e.Count / 2], e[Math.Min(e.Count - 1, (int)Math.Ceiling(0.95 * e.Count) - 1)], e.Count);
    }
}
