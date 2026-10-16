using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-facade's hooks into the engine (EngineCore.Modules.cs): the site verbs, STATE.stage.site, controls and surveys,
/// each projector's uncertainty and priors, the site and survey context items, and the check of a control point or a bound pin an
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
            w.WriteEndObject();
        }
        else
        {
            w.WriteNullValue();
        }
        w.WriteNumber("controls", stage.Controls.Length);
        w.WritePropertyName("surveys");
        WriteSurveyRows(w, stage, provenance: false);
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
    }

    partial void ContextSite(List<ContextItem> items, List<ContextLink> links)
    {
        var p = Store.Project;
        var stage = p.Stage;
        var check = Volatile.Read(ref _siteCheck);
        var georeferenced = stage.Surveys.Any(s => s.Crs.StartsWith("EPSG:", StringComparison.Ordinal));
        if (stage.Site.IsSet || georeferenced || stage.Controls.Length > 0)
        {
            var light = !stage.Site.IsSet && georeferenced ? Light.Amber
                : check is { Revision: var rev } && rev == Store.Current.Revision ? check.Light
                : Light.Green;
            var state = !stage.Site.IsSet ? "a georeferenced survey has no site — SITE ORIGIN sets it"
                : string.Create(CultureInfo.InvariantCulture, $"{stage.Site.Crs} at {stage.Site.Origin.X:0.###} {stage.Site.Origin.Y:0.###} {stage.Site.Origin.Z:0.###}; {stage.Controls.Length} control points")
                  + (check is not null && check.Revision == Store.Current.Revision ? "; " + check.Words : "");
            items.Add(new ContextItem("site", "site", "Site", light, state));
        }
        foreach (var row in SurveyRows(stage))
        {
            var light = row.State.StartsWith("importing", StringComparison.Ordinal) ? Light.Amber
                : row.State.StartsWith("failed", StringComparison.Ordinal) ? Light.Red
                : Light.Green;
            items.Add(new ContextItem("survey:" + row.Id, "survey", row.Name, light, row.State));
        }
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
            case CalibrationPin pin when pin.ControlId.Length > 0 && !p.Stage.Controls.Any(c => c.Id == pin.ControlId):
                why = $"no control point {pin.ControlId} — SITE POINTS imports them";
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
