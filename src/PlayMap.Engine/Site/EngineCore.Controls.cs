using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Site;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>SITE POINTS (surveyed control points into the project) and SITE CHECK (the model measured against them). A point imported
/// again under its name moves, and every pin and position prior bound to it follows in the same edit.</summary>
public sealed partial class EngineCore
{
    /// <summary>The last SITE CHECK: the project revision it measured, its light and its words (for the site's context item and
    /// preflight).</summary>
    internal sealed record SiteCheckFact(long Revision, Light Light, string Words, SiteCheckResult Result);

    private SiteCheckFact? _siteCheck;

    internal SiteCheckFact? LastSiteCheck => Volatile.Read(ref _siteCheck);

    private async Task<CommandReply> SitePointsAsync(VerbCall call)
    {
        const string Usage = "SITE POINTS <path> [ORDER PENZD|PNEZD|ENH|NEH] [CRS <code>] [UNITS m|cm|mm|ft|usft]";
        var args = call.Args;
        if (args.Count < 1)
            return CommandReply.Fail(Usage);
        ControlOrder? order = null;
        string? crs = null;
        var unit = 1.0;
        for (var i = 1; i < args.Count; i++)
        {
            if (i + 1 >= args.Count)
                return CommandReply.Fail(Usage);
            var word = args[i].ToUpperInvariant();
            var value = args[++i];
            switch (word)
            {
                case "ORDER":
                    order = ControlPointFile.OrderOf(value);
                    if (order is null)
                        return CommandReply.Fail($"ORDER takes PENZD, PNEZD, ENH or NEH — not {value}");
                    break;
                case "CRS":
                    crs = CrsWord(value);
                    if (crs is null)
                        return CommandReply.Fail($"'{value}' is not a coordinate system — EPSG:<code> or LOCAL");
                    break;
                case "UNITS":
                    if (ControlPointFile.UnitScale(value) is not { } s)
                        return CommandReply.Fail($"UNITS takes m, cm, mm, ft or usft — not {value}");
                    unit = s;
                    break;
                default:
                    return CommandReply.Fail(Usage);
            }
        }
        var path = args[0];
        ControlRead read;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return CommandReply.Fail($"no file {path} on this machine");
            if (info.Length > 16L * 1024 * 1024)
                return CommandReply.Fail($"{path} is larger than 16 MB — a control file is a list of points");
            using var reader = new StreamReader(path);
            read = ControlPointFile.Parse(reader, order, unit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException)
        {
            return CommandReply.Fail($"{Path.GetFileName(path)}: {ex.Message}");
        }
        if (read.Points.Count == 0)
            return CommandReply.Fail($"{Path.GetFileName(path)} holds no control points ({read.Skipped.Count} lines skipped)");
        var ids = new List<string>();
        int added = 0, updated = 0;
        var setSite = false;
        var r = await EditAsync(call.Session, $"control points from {Path.GetFileName(path)}", p =>
        {
            ids.Clear();
            added = 0;
            updated = 0;
            var site = p.Stage.Site;
            if (crs is not null && site.IsSet && crs.StartsWith("EPSG:", StringComparison.Ordinal) && site.Crs.StartsWith("EPSG:", StringComparison.Ordinal)
                && crs != site.Crs)
                throw new EditRefusedException($"the points are in {crs} but the site is in {site.Crs} — export them in the site's system");
            setSite = !site.IsSet;
            if (setSite)
            {
                var c = new Vec3d(read.Points.Average(x => x.Grid.X), read.Points.Average(x => x.Grid.Y), read.Points.Average(x => x.Grid.Z));
                static double Round(double v) => Math.Round(v / GeometryReadOptions.OriginRoundingM, MidpointRounding.AwayFromZero) * GeometryReadOptions.OriginRoundingM;
                site = new SiteFrame(crs ?? "LOCAL", "", new Vec3d(Round(c.X), Round(c.Y), Round(c.Z)), 0, 1, site.MainsHz);
            }
            var controls = p.Stage.Controls;
            var taken = new HashSet<string>(p.AllIds(), StringComparer.Ordinal);
            foreach (var row in read.Points)
            {
                if (controls.FirstOrDefault(c => c.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase)) is { } have)
                {
                    ids.Add(have.Id);
                    if (have.Grid == row.Grid && have.Code == row.Code)
                        continue;
                    controls = controls.Update(have.Id, c => c with { Grid = row.Grid, Code = row.Code });
                    updated++;
                    continue;
                }
                string id;
                do
                    id = Ids.New(Ids.ControlPoint);
                while (!taken.Add(id));
                controls = controls.Add(new ControlPoint(id, row.Name, row.Grid, 0.01, row.Code, true));
                ids.Add(id);
                added++;
            }
            if (added == 0 && updated == 0)
                throw new EditRefusedException($"all {read.Points.Count} points are in the project as they are — this would change nothing");
            return p.WithStage(st => Core.Site.ControlBinding.Rebind(st with { Controls = controls, Site = site }));
        }).ConfigureAwait(false);
        if (!r.Changed)
            return FromEdit(r);
        var words = string.Create(SiteInv, $"{added} control point{(added == 1 ? "" : "s")} added, {updated} moved, from {Path.GetFileName(path)} ({read.Order.ToString().ToUpperInvariant()} from the {read.OrderFrom})")
            + (read.Skipped.Count > 0 ? string.Create(SiteInv, $"; {read.Skipped.Count} line{(read.Skipped.Count == 1 ? "" : "s")} skipped") : "")
            + (setSite ? $"; the site is {Store.Project.Stage.Site.Crs} at the points' centre" : "");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("added", added);
            w.WriteNumber("updated", updated);
            w.WriteStartArray("ids");
            foreach (var id in ids)
                w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteStartArray("skipped");
            foreach (var s in read.Skipped)
            {
                w.WriteStartObject();
                w.WriteNumber("line", s.Line);
                w.WriteString("why", s.Why);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("order", read.Order.ToString().ToUpperInvariant());
            w.WriteString("orderFrom", read.OrderFrom);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>The model SITE CHECK measures: a surface, a survey's surfaces (or its thinned cloud when it has no mesh), or every
    /// visible surface on a mesh; its nearest-point function and its name; null with the reason.</summary>
    private (Func<Vec3d, (Vec3d Point, double Distance)> Nearest, string Name)? SiteModelOf(Project p, string? word, out string why)
    {
        why = "";
        var stage = p.Stage;
        List<Surface> surfaces;
        string name;
        if (word is null)
        {
            surfaces = [.. stage.Surfaces.Where(s => s.Visible && s.Pixel is null)];
            name = "the stage";
            if (surfaces.Count == 0 && stage.Surveys.FirstOrDefault(s => s.CloudPath.Length > 0) is { } only)
                return CloudModel(p, only, out why);
        }
        else if ((stage.Surfaces.Find(word) ?? stage.Surfaces.FirstOrDefault(s => s.Name.Equals(word, StringComparison.OrdinalIgnoreCase))) is { } one)
        {
            surfaces = [one];
            name = one.Name;
        }
        else if ((stage.Surveys.Find(word) ?? stage.Surveys.FirstOrDefault(s => s.Name.Equals(word, StringComparison.OrdinalIgnoreCase))) is { } sv)
        {
            if (sv.MeshId is null)
                return CloudModel(p, sv, out why);
            surfaces = [.. stage.Surfaces.Where(s => s.MeshId == sv.MeshId && s.Pixel is null)];
            name = sv.Name;
        }
        else
        {
            why = $"no surface or survey {word}";
            return null;
        }
        if (surfaces.Count == 0)
        {
            why = "no surface to measure — SURVEY IMPORT a model first";
            return null;
        }
        var scene = StageScene.Build(stage with { Surfaces = [.. surfaces] }, Meshes.For(stage), includeHidden: word is not null);
        if (scene.World.TriangleCount == 0)
        {
            why = "the model's meshes did not load: " + string.Join("; ", scene.Problems);
            return null;
        }
        var distance = new MeshDistance(scene.World);
        return (distance.Closest, name);
    }

    private (Func<Vec3d, (Vec3d Point, double Distance)> Nearest, string Name)? CloudModel(Project p, SurveySource sv, out string why)
    {
        why = "";
        if (ProjectFolderForFiles is not { } folder || !File.Exists(Path.Combine(folder, sv.CloudPath)))
        {
            why = $"{sv.Name}'s cloud ({sv.CloudPath}) is not beside the project";
            return null;
        }
        PointCloud cloud;
        using (var stream = File.OpenRead(Path.Combine(folder, sv.CloudPath)))
            cloud = MeshIO.ReadPly(stream, new GeometryReadOptions(1, true, p.Stage.Site)).ToPointCloud();
        var tree = new KdTree3(cloud.Points);
        return (q =>
        {
            var i = tree.Nearest(q, out var d2);
            return i < 0 ? (q, double.PositiveInfinity) : (cloud.Points[i], Math.Sqrt(d2));
        }, sv.Name + "'s cloud");
    }

    /// <summary>SITE CHECK [&lt;surface&gt;|&lt;survey&gt;]: every enabled control point's distance to the model, its light, and the fit; kept
    /// for the site's context item and preflight until the project changes. Off the lane: the meshes load and the distances are
    /// measured on a worker.</summary>
    private async Task<CommandReply> SiteCheckAsync(VerbCall call)
    {
        if (call.Args.Count > 1)
            return CommandReply.Fail("SITE CHECK [<surface>|<survey>]");
        var snap = Store.Current;
        var word = call.Args.Count == 1 ? call.Args[0] : null;
        if (snap.Project.Stage.Controls.Length == 0)
            return CommandReply.Fail("the project has no control points — SITE POINTS <path> imports them");
        var outcome = await Task.Run(() => RunSiteCheck(snap.Project, word), call.Ct).ConfigureAwait(false);
        if (outcome.Why is { } why)
            return CommandReply.Fail(why);
        var result = outcome.Result!;
        Volatile.Write(ref _siteCheck, new SiteCheckFact(snap.Revision, result.Light, result.Words, result));
        FactsChanged();
        return CommandReply.Success(JsonOut(w => WriteSiteCheck(w, result)), result.Words);
    }

    internal (SiteCheckResult? Result, string? Why) RunSiteCheck(Project p, string? word)
    {
        try
        {
            if (SiteModelOf(p, word, out var why) is not { } model)
                return (null, why);
            return (SiteCheck.Run(p.Stage.Controls, p.Stage.Site, model.Nearest, model.Name), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or NotSupportedException)
        {
            return (null, "the model could not be read: " + ex.Message);
        }
    }

    private static void WriteSiteCheck(Utf8JsonWriter w, SiteCheckResult result)
    {
        w.WriteStartObject();
        w.WriteStartArray("points");
        foreach (var row in result.Points)
        {
            w.WriteStartObject();
            w.WriteString("id", row.Id);
            w.WriteString("name", row.Name);
            w.WriteNumber("distanceM", double.IsFinite(row.DistanceM) ? Math.Round(row.DistanceM, 4) : -1);
            w.WriteString("light", row.Light.ToString().ToLowerInvariant());
            w.WriteString("words", row.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartObject("fit");
        w.WriteNumber("shiftM", Math.Round(result.ShiftM, 4));
        w.WriteNumber("rotDeg", Math.Round(result.RotationDeg, 4));
        w.WriteNumber("scalePpm", Math.Round(result.ScalePpm, 1));
        w.WriteEndObject();
        w.WriteString("light", result.Light.ToString().ToLowerInvariant());
        w.WriteString("words", result.Words);
        w.WriteEndObject();
    }
}
