using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>PLAN LIGHT: the predicted illuminance, luminance, contrast and pixel pitch per surface from the projectors as placed
/// (<see cref="LightPlan"/>), a query worked off the lane on the session's own project.</summary>
public sealed partial class EngineCore
{
    private async Task<CommandReply> PlanLightAsync(VerbCall c)
    {
        const string Usage = "PLAN LIGHT [<surface>|ALL] [AMBIENT <lux>] [ALBEDO <0-1>] [TARGET <lux>] [DERATE <0-1>]";
        var args = c.Args.ToList();
        var options = LightPlanOptions.Default;
        foreach (var (word, apply) in new (string, Func<double, LightPlanOptions?>)[]
        {
            ("AMBIENT", v => v >= 0 ? options with { AmbientLux = v } : null),
            ("ALBEDO", v => v is >= 0 and <= 1 ? options with { Albedo = v } : null),
            ("TARGET", v => v > 0 ? options with { TargetLux = v } : null),
            ("DERATE", v => v is > 0 and <= 1 ? options with { Derate = v } : null),
        })
        {
            if (TakeOption(args, word) is not { } value)
                continue;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || apply(v) is not { } next)
                return CommandReply.Fail($"{word} {value}: out of range — {Usage}");
            options = next;
        }
        if (args.Count > 1)
            return CommandReply.Fail(Usage);
        var p = await _lane.RunAsync(() => EditsFor(c.Session).Project).ConfigureAwait(false);
        List<string>? only = null;
        if (args.Count == 1 && !args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            var surface = p.Stage.Surfaces.Find(args[0]) ?? p.Stage.Surfaces.FirstOrDefault(s => s.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
            if (surface is null)
                return CommandReply.Fail($"no surface {args[0]}");
            only = [surface.Id];
        }
        if (p.Stage.Projectors.IsEmpty)
            return CommandReply.Fail("no projector on the stage to plan light from");
        var report = await Task.Run(() =>
        {
            var scene = StageScene.Build(p.Stage, Meshes.For(p.Stage), includeHidden: only is not null);
            return LightPlan.Analyse(scene, p.Stage, only, options, c.Ct);
        }, c.Ct).ConfigureAwait(false);
        if (report.Surfaces.Count == 0)
            return CommandReply.Fail("no visible surface to light — the model's meshes did not load, or none is visible");
        return CommandReply.Success(JsonOut(w =>
        {
            static double R(double v, int digits) => double.IsFinite(v) ? Math.Round(v, digits) : -1;
            w.WriteStartObject();
            w.WriteStartArray("surfaces");
            foreach (var s in report.Surfaces)
            {
                w.WriteStartObject();
                w.WriteString("id", s.SurfaceId);
                w.WriteString("name", s.Name);
                w.WriteNumber("areaM2", R(s.AreaM2, 2));
                w.WriteNumber("coverage", R(s.Coverage, 3));
                w.WriteNumber("luxP5", R(s.LuxP5, 1));
                w.WriteNumber("luxP50", R(s.LuxP50, 1));
                w.WriteNumber("luxP95", R(s.LuxP95, 1));
                w.WriteNumber("nitsP50", R(s.NitsP50, 1));
                w.WriteNumber("contrastP5", R(s.ContrastP5, 2));
                w.WriteNumber("pitchMmP50", R(s.PitchMmP50, 2));
                w.WriteNumber("pitchMmP95", R(s.PitchMmP95, 2));
                w.WriteNumber("stackDepth", s.StackDepth);
                w.WriteNumber("deficitLm", R(s.DeficitLm, 0));
                w.WriteString("words", s.Words);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("words", report.Words);
            w.WriteEndObject();
        }), report.Words);
    }
}
