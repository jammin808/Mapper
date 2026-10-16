using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-facade's hooks into the engine (EngineCore.Modules.cs): the site and stack verbs, STATE.stage.site, controls, surveys
/// and stacks, each projector's uncertainty, priors and stack, the site, survey and stack context items, and the check of a control point or a bound pin an
/// edit adds.</summary>
public sealed partial class EngineCore
{
    partial void RouteSite(VerbRoutes routes)
    {
        routes.Lane("SITE", _ => SiteQuery());
        routes.Lane("SITE ORIGIN", c => SiteOrigin(c.Args));
        routes.Lane("SITE MAINS", c => SiteMains(c.Args));
        routes.OffLane("SITE POINTS", SitePointsAsync);
        routes.OffLane("SITE CHECK", SiteCheckAsync);
        routes.Lane("SURVEY IMPORT", SurveyImport);
        routes.Lane("SURVEY LIST", _ => SurveyList());
        routes.Lane("PROJECTOR PRIOR", c => ProjectorPrior(c.Args));
        routes.Lane("SITE GUIDE", SiteGuideQuery);
        RouteStacks(routes);
    }

    partial void StageSite(Utf8JsonWriter w)
    {
        var stage = Store.Project.Stage;
        w.WritePropertyName("site");
        if (stage.Site.IsSet)
        {
            w.WriteStartObject();
            w.WriteString("crs", stage.Site.Crs);
            w.WriteString("name", stage.Site.CrsName);
            w.WriteStartArray("origin");
            w.WriteNumberValue(stage.Site.Origin.X);
            w.WriteNumberValue(stage.Site.Origin.Y);
            w.WriteNumberValue(stage.Site.Origin.Z);
            w.WriteEndArray();
            w.WriteNumber("rotationDeg", stage.Site.RotationDeg);
            w.WriteNumber("gridScale", stage.Site.GridScale);
            w.WriteNumber("mainsHz", stage.Site.MainsHz);
            w.WritePropertyName("next");
            if (Core.Site.SiteGuide.Next(GuideFor(Store.Project)) is { } next)
                WriteGuideStep(w, next);
            else
                w.WriteNullValue();
            w.WriteEndObject();
        }
        else
        {
            w.WriteNullValue();
        }
        w.WriteNumber("controls", stage.Controls.Length);
        w.WritePropertyName("surveys");
        WriteSurveyRows(w, stage, provenance: false);
        w.WritePropertyName("stacks");
        WriteStacks(w, Store.Project, list: false);
    }

    partial void ProjectorSite(Utf8JsonWriter w, Projector pj)
    {
        w.WritePropertyName("uncertainty");
        if (pj.Uncertainty is { } u)
        {
            w.WriteStartObject();
            w.WriteNumber("wallPx", Math.Round(u.WallPx, 3));
            w.WriteNumber("axisM", Math.Round(u.AxisM, 3));
            w.WriteNumber("focalPct", Math.Round(u.FocalPct, 3));
            w.WriteString("weak", u.Weak);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNullValue();
        }
        w.WriteString("priors", PriorWords(Store.Project, pj.Priors));
        if (Store.Project.Stage.Stacks.FirstOrDefault(s => s.ProjectorIds.Contains(pj.Id, StringComparer.Ordinal)) is { } stack)
            w.WriteString("stack", stack.Id);
        else
            w.WriteNull("stack");
    }

    partial void ContextSite(List<ContextItem> items, List<ContextLink> links)
    {
        var p = Store.Project;
        var stage = p.Stage;
        var check = Volatile.Read(ref _siteCheck);
        var georeferenced = stage.Surveys.Any(s => s.Crs.StartsWith("EPSG:", StringComparison.Ordinal));
        if (stage.Site.IsSet || georeferenced || stage.Controls.Length > 0)
        {
            var current = check is not null && check.Revision == Store.Current.Revision;
            // Unknown stays unknown: a check made before the last change says nothing about the stage now.
            var light = !stage.Site.IsSet && georeferenced ? Light.Amber
                : current ? check!.Light
                : stage.Controls.Length > 0 ? Light.Grey
                : Light.Green;
            var state = !stage.Site.IsSet ? "a georeferenced survey has no site — SITE ORIGIN sets it"
                : string.Create(CultureInfo.InvariantCulture, $"{stage.Site.Crs} at {stage.Site.Origin.X:0.###} {stage.Site.Origin.Y:0.###} {stage.Site.Origin.Z:0.###}; {stage.Controls.Length} control points")
                  + (current ? "; " + check!.Words : stage.Controls.Length > 0 ? "; not checked since the last change — SITE CHECK" : "");
            items.Add(new ContextItem("site", "site", "Site", light, state));
        }
        foreach (var row in SurveyRows(stage))
        {
            var light = row.State.StartsWith("importing", StringComparison.Ordinal) ? Light.Amber
                : row.State.StartsWith("failed", StringComparison.Ordinal) ? Light.Red
                : Light.Green;
            items.Add(new ContextItem("survey:" + row.Id, "survey", row.Name, light, row.State));
        }
        ContextStacks(p, items);
    }

    partial void ValidateSite(Project p, object entity, ref string? why)
    {
        switch (entity)
        {
            case ControlPoint cp when !double.IsFinite(cp.Grid.X) || !double.IsFinite(cp.Grid.Y) || !double.IsFinite(cp.Grid.Z):
                why = $"control point {cp.Name} needs finite grid coordinates";
                break;
            case ControlPoint cp when cp.SigmaM < 0 || !double.IsFinite(cp.SigmaM):
                why = $"control point {cp.Name}'s σ is a distance of 0 or more";
                break;
            case CalibrationPin pin when pin.ControlId.Length > 0 && p.Stage.Controls.Find(pin.ControlId) is null:
                why = $"no control point {pin.ControlId} — SITE POINTS imports them";
                break;
            case CalibrationPin pin when pin.ControlId.Length > 0 && p.Stage.Controls.Find(pin.ControlId) is { } bound
                && (pin.World - p.Stage.Site.ToStage(bound.Grid)).Length > BoundPinToleranceM:
            {
                var at = p.Stage.Site.ToStage(bound.Grid);
                why = string.Create(SiteInv, $"a pin bound to {bound.Name} sits at it: world {{\"x\":{at.X:R},\"y\":{at.Y:R},\"z\":{at.Z:R}}}");
                break;
            }
            case StackGroup sk when !sk.ProjectorIds.IsDefault && sk.ProjectorIds.FirstOrDefault(id => p.FindProjector(id) is null) is { } missing:
                why = $"stack {sk.Name}: no projector {missing}";
                break;
            case StackGroup sk when !sk.ProjectorIds.IsDefault && sk.ProjectorIds.Distinct(StringComparer.Ordinal).Count() != sk.ProjectorIds.Length:
                why = $"stack {sk.Name} names a projector twice";
                break;
            case StackGroup sk when sk.MasterId.Length > 0 && !sk.ProjectorIds.IsDefault && !sk.ProjectorIds.Contains(sk.MasterId, StringComparer.Ordinal):
                why = $"stack {sk.Name}'s master must be one of its members";
                break;
            case StackGroup sk when !sk.ProjectorIds.IsDefault && p.Stage.Stacks.FirstOrDefault(o => o.Id != sk.Id && o.ProjectorIds.Intersect(sk.ProjectorIds, StringComparer.Ordinal).Any()) is { } other:
                why = $"a projector of {sk.Name} is in stack {other.Name} already";
                break;
            case ControlPoint cp when p.Stage.Controls.Find(cp.Id) is { } was && was.Grid != cp.Grid
                && p.Stage.Projectors.Any(pj => pj.Priors.ControlId == cp.Id || pj.Pins.Any(x => x.ControlId == cp.Id)):
                why = $"pins or priors are bound to {cp.Name} — re-survey it with SITE POINTS, so they follow it";
                break;
        }
    }

    /// <summary>A projector's priors in words for STATE ("TR 3.02 ±2 %", "at CP7 ±5 cm"), "" when it has none.</summary>
    internal static string PriorWords(Project p, SolvePriors priors)
    {
        var parts = new List<string>();
        if (priors.ThrowRatio > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"TR {priors.ThrowRatio:0.###} ±{(priors.ThrowRatioSigmaPct > 0 ? priors.ThrowRatioSigmaPct : Core.Calibration.PosePriors.DefaultThrowSigmaPct):0.##} %"));
        if (priors.Position is { } at)
        {
            var sigma = priors.PositionSigmaM > 0 ? priors.PositionSigmaM : Core.Calibration.PosePriors.DefaultPositionSigmaM;
            var where = priors.ControlId.Length > 0
                ? "at " + (p.Stage.Controls.Find(priors.ControlId)?.Name ?? priors.ControlId)
                : string.Create(CultureInfo.InvariantCulture, $"at {at.X:0.##} {at.Y:0.##} {at.Z:0.##}");
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{where} ±{sigma * 100:0.#} cm"));
        }
        return string.Join(" · ", parts);
    }
}
