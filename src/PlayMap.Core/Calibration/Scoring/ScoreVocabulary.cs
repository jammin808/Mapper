using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using PlayMap.Core.Model;

namespace PlayMap.Core.Calibration.Scoring;

/// <summary>One term code's vocabulary: its title, the template its words are made from (composite-format holes, in the order
/// the code's builder passes them), its value's unit, the verb line that fixes it (holes {target}, {pin}, {camera}, {group},
/// {stack}, {scan}) and what hands do when the line is not enough.</summary>
public sealed record ScoreVocabularyEntry(string Code, string Title, string Template, string Unit, string Fix, string Hint)
{
    /// <summary>The words for these values (invariant culture).</summary>
    public string Words(params object[] values) => string.Format(CultureInfo.InvariantCulture, Template, values);
}

/// <summary>The words, units and fix lines of every <see cref="ScoreTermCodes"/> code. The desk, Companion and the assistant word a
/// scan's, an alignment's and a pin solve's score from this one table; every fix line is a verb line of the catalogue.</summary>
public static class ScoreVocabulary
{
    public static readonly FrozenDictionary<string, ScoreVocabularyEntry> Entries = new ScoreVocabularyEntry[]
    {
        // pins
        new(ScoreTermCodes.Rms, "fit", "fits its {0} pins to {1:0.00} px", "px",
            "CALIBRATION RESOLVE {target}", "drag the pins that disagree onto their features, then solve again"),
        new(ScoreTermCodes.P95, "worst pins", "95 % of its pins within {0:0.0} px; pin {1} is off by {2:0.0} px", "px",
            "SET {pin} enabled false", "or drag pin {pinNo} onto its feature"),
        new(ScoreTermCodes.Coverage, "coverage", "pins in {0:0} % of the picture's cells{1}", "%",
            "PROJECTOR SOLVE {target} LOCK SHIFT", "add pins in the empty part of the picture"),
        new(ScoreTermCodes.Conditioning, "conditioning", "could be {0:0.0} px off at the edges{1}", "px",
            "PROJECTOR SOLVE {target} LOCK FOCAL", "or add a pin off this plane"),
        new(ScoreTermCodes.Lens, "lens", "{0}", "", "PROJECTOR SOLVE {target} LOCK SHIFT",
            "check the projector's zoom, shift and scaling, or lock them"),
        new(ScoreTermCodes.Outliers, "outliers", "pin {0} disagrees by {1:0} px{2}", "pins",
            "SET {pin} enabled false", "or drag pin {pinNo} onto its feature"),
        new(ScoreTermCodes.Geometry, "geometry", "{0}", "", "CALIBRATION FALLBACK {target}",
            "the pins disagree on where the projector is: check which point of the stage each one names"),
        new(ScoreTermCodes.Spread, "spread", "the pins cover {0:0} % of the picture{1}", "%",
            "PROJECTOR SOLVE {target} LOCK FOCAL", "add pins in the empty quarters"),
        // scans
        new(ScoreTermCodes.DenseRms, "dense fit", "the scan fits {0:N0} points to {1:0.00} px", "px",
            "SCAN START {camera} {target} MODE ROBUST", "keep the camera and the room still while it scans"),
        new(ScoreTermCodes.Decoded, "decoded", "{0:0} % of the projector's picture decoded", "%",
            "SCAN START {camera} {target} MODE DIFFICULT", "darken the room, or lower the camera's exposure"),
        new(ScoreTermCodes.Views, "views", "{0} view{1}: depth from one camera position", "views",
            "SCAN START {camera} {target}", "move the camera and scan again from another side"),
        // alignment and stacks
        new(ScoreTermCodes.Seam, "seam", "neighbours disagree by {0:0.0} px in their overlap", "px",
            "ALIGN RUN {group}", "check the overlap is in the camera's view"),
        new(ScoreTermCodes.StackConvergence, "stack", "the stack's members disagree by {0:0.0} px", "px",
            "STACK ALIGN {stack} FROM {scan}", "check every member of the stack lit the scan"),
        new(ScoreTermCodes.Uniformity, "uniformity", "brightness varies {0:0} % across the picture", "%",
            "ALIGN RUN {group} STEPS COLOR", "match the projectors' lamp modes and ages"),
        new(ScoreTermCodes.ColourMatch, "colour", "colours differ by ΔE {0:0.0} between outputs", "ΔE",
            "ALIGN RUN {group} COLOR WHITE", "set every projector to the same colour mode"),
    }.ToFrozenDictionary(e => e.Code, StringComparer.Ordinal);

    public static ScoreVocabularyEntry Of(string code) =>
        Entries.TryGetValue(code, out var e) ? e : throw new ArgumentException($"{code} is not a score term code", nameof(code));

    /// <summary>A fix or hint template with its holes filled ("{target}" → the projector's id, …); a template with a hole that has
    /// no value gives "" (no line), so a line never carries a hole.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> holes)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(holes);
        var sb = new StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }
            var close = template.IndexOf('}', open);
            if (close < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }
            sb.Append(template, i, open - i);
            var name = template.Substring(open + 1, close - open - 1);
            if (holes.TryGetValue(name, out var value) && value.Length > 0)
                sb.Append(value);
            else
                return "";
            i = close + 1;
        }
        return sb.ToString();
    }
}
