using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Site;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>The site frame's verbs: SITE answers it, SITE ORIGIN sets it (the stage moves with it in one edit, so every grid position
/// and every pin's reprojection is kept), SITE MAINS sets the venue's mains frequency. A verb that would change nothing is refused
/// with the reason.</summary>
public sealed partial class EngineCore
{
    private static readonly CultureInfo SiteInv = CultureInfo.InvariantCulture;

    private CommandReply SiteQuery()
    {
        var site = Edits.Project.Stage.Site;
        return CommandReply.Success(JsonOut(w => WriteSite(w, site)));
    }

    internal static void WriteSite(Utf8JsonWriter w, SiteFrame site)
    {
        w.WriteStartObject();
        w.WriteString("crs", site.Crs);
        w.WriteString("crsName", site.CrsName);
        w.WriteStartArray("origin");
        w.WriteNumberValue(site.Origin.X);
        w.WriteNumberValue(site.Origin.Y);
        w.WriteNumberValue(site.Origin.Z);
        w.WriteEndArray();
        w.WriteNumber("rotationDeg", site.RotationDeg);
        w.WriteNumber("gridScale", site.GridScale);
        w.WriteNumber("mainsHz", site.MainsHz);
        w.WriteBoolean("set", site.IsSet);
        w.WriteEndObject();
    }

    /// <summary>"EPSG:25832" (any EPSG code) or "LOCAL", normalised; null when it is neither.</summary>
    internal static string? CrsWord(string word)
    {
        if (word.Equals("LOCAL", StringComparison.OrdinalIgnoreCase))
            return "LOCAL";
        if (word.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(word.AsSpan(5), NumberStyles.None, SiteInv, out var code) && code > 0)
            return "EPSG:" + code.ToString(SiteInv);
        return null;
    }

    /// <summary>A site edit's words as its reply says them: from a blind draft, staged — the stage takes it when the draft lands.</summary>
    private static string Staged(bool staged, string words) => staged ? $"staged in the blind draft: {words} — on the stage {WhenLanded}" : words;

    private static bool TryNumber(string word, out double value) =>
        double.TryParse(word, NumberStyles.Float, SiteInv, out value) && double.IsFinite(value);

    /// <summary>SITE ORIGIN &lt;e&gt; &lt;n&gt; &lt;h&gt; [CRS &lt;code&gt;|LOCAL] [ROTATE &lt;deg&gt;]. With a site set, the whole stage moves
    /// so every grid position stays where it is; with none, the stage's coordinates are taken as already relative to the new origin
    /// (nothing moves). Pins bound to control points are re-derived in the same edit.</summary>
    private CommandReply SiteOrigin(List<string> args)
    {
        const string Usage = "SITE ORIGIN <e> <n> <h> [CRS <code>|LOCAL] [ROTATE <deg>]";
        if (args.Count < 3 || !TryNumber(args[0], out var e) || !TryNumber(args[1], out var n) || !TryNumber(args[2], out var h))
            return CommandReply.Fail(Usage);
        var before = Edits.Project.Stage.Site;
        var crs = before.IsSet ? before.Crs : "LOCAL";
        var rotation = before.RotationDeg;
        for (var i = 3; i < args.Count; i++)
        {
            var word = args[i].ToUpperInvariant();
            if (word == "LOCAL")
            {
                crs = "LOCAL";
            }
            else if (word == "CRS" && i + 1 < args.Count)
            {
                crs = CrsWord(args[++i]) ?? throw new EditRefusedException($"'{args[i]}' is not a coordinate system — EPSG:<code> or LOCAL");
            }
            else if (word == "ROTATE" && i + 1 < args.Count && TryNumber(args[i + 1], out var deg))
            {
                rotation = deg;
                i++;
            }
            else
            {
                return CommandReply.Fail(Usage);
            }
        }
        rotation = ((rotation % 360) + 360) % 360;
        if (before.IsSet && before.Crs.StartsWith("EPSG:", StringComparison.Ordinal) && crs.StartsWith("EPSG:", StringComparison.Ordinal) && crs != before.Crs)
            return CommandReply.Fail($"the site is in {before.Crs} and its control points and surveys are in its numbers — moving to {crs} is a reprojection, which is not done here");
        var after = new SiteFrame(crs, crs == before.Crs ? before.CrsName : "", new Vec3d(e, n, h), rotation, before.GridScale, before.MainsHz);
        if (before.IsSet && after == before)
            return CommandReply.Fail(string.Create(SiteInv, $"the site is at {e:0.###} {n:0.###} {h:0.###} ({crs}) already — this would change nothing"));
        var move = before.IsSet ? StageMove.Between(before, after) : StageMove.None;
        var r = Edits.Apply("site origin", p => p.WithStage(st => Core.Site.ControlBinding.Rebind(move.Apply(st) with { Site = after })));
        if (!r.Changed)
            return FromEdit(r);
        var staged = EditingBlind;
        var moved = move.IsNone ? Vec3d.Zero : move.T;
        var scans = Edits.Project.Stage.Scans.Length;
        var words = before.IsSet
            ? string.Create(SiteInv, $"site origin {e:0.###} {n:0.###} {h:0.###} {crs}; the stage moved by {moved.X:0.###} {moved.Y:0.###} {moved.Z:0.###} m so every grid position is kept")
              + (scans > 0 && !move.IsNone ? string.Create(SiteInv, $"; the {scans} scan{(scans == 1 ? "" : "s")} taken before keep{(scans == 1 ? "s" : "")} the old frame's points — scan again before using them") : "")
            : string.Create(SiteInv, $"site origin {e:0.###} {n:0.###} {h:0.###} {crs}; the stage's coordinates are now relative to it (nothing moved)");
        words = Staged(staged, words);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("origin");
            w.WriteNumberValue(e);
            w.WriteNumberValue(n);
            w.WriteNumberValue(h);
            w.WriteEndArray();
            w.WriteString("crs", crs);
            w.WriteNumber("rotationDeg", rotation);
            w.WriteStartArray("moved");
            w.WriteNumberValue(Math.Round(moved.X, 6));
            w.WriteNumberValue(Math.Round(moved.Y, 6));
            w.WriteNumberValue(Math.Round(moved.Z, 6));
            w.WriteEndArray();
            w.WriteBoolean("staged", staged);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    private CommandReply SiteMains(List<string> args)
    {
        if (args.Count != 1 || !int.TryParse(args[0], NumberStyles.None, SiteInv, out var hz) || hz is not (50 or 60))
            return CommandReply.Fail("SITE MAINS 50|60");
        var site = Edits.Project.Stage.Site;
        if (site.MainsHz == hz)
            return CommandReply.Fail($"the mains are {hz} Hz already — this would change nothing");
        var r = Edits.Apply($"site mains {hz} Hz", p => p.WithStage(st => st with { Site = st.Site with { MainsHz = hz } }));
        var staged = EditingBlind;
        return r.Changed
            ? CommandReply.Success(JsonOut(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("mainsHz", hz);
                w.WriteBoolean("staged", staged);
                w.WriteEndObject();
            }), Staged(staged, $"mains {hz} Hz"))
            : FromEdit(r);
    }
}
