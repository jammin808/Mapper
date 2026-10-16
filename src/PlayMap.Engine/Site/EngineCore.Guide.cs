using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Site;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;

namespace PlayMap.Engine;

/// <summary>SITE GUIDE (alias FACADE GUIDE): the facade workflow as steps, each with its state and the line that does it, read from the
/// session's own project (a blind draft's session reads what it staged); STATE.stage.site.next is the live stage's next step.</summary>
public sealed partial class EngineCore
{
    /// <summary>The guide's facts for a project: the blend in place solved for its stage, and each projector's current score.</summary>
    internal GuideFacts GuideFactsFor(Project p) =>
        new(LastBlendSolve is { } solved && solved.Inputs.Equals(BlendInputs.Of(p)),
            id => p.FindProjector(id)?.CalibrationId is null ? null : CalibrationLedgerService.ScoreOf(p, id));

    internal IReadOnlyList<GuideStep> GuideFor(Project p) => SiteGuide.Steps(p, GuideFactsFor(p));

    private CommandReply SiteGuideQuery(VerbCall c)
    {
        if (c.Args.Count != 0)
            return CommandReply.Fail("SITE GUIDE");
        var steps = GuideFor(Edits.Project);
        var next = SiteGuide.Next(steps);
        var words = next is null ? "the facade is through every step" : $"next: {next.Words} — {next.Line}";
        if (EditingBlind)
            words = "in the blind draft: " + words;
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("steps");
            foreach (var s in steps)
                WriteGuideStep(w, s);
            w.WriteEndArray();
            w.WritePropertyName("next");
            if (next is null)
                w.WriteNullValue();
            else
                WriteGuideStep(w, next);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    internal static void WriteGuideStep(Utf8JsonWriter w, GuideStep s)
    {
        w.WriteStartObject();
        w.WriteString("step", s.Step);
        w.WriteString("state", s.State);
        w.WriteString("words", s.Words);
        w.WriteString("line", s.Line);
        w.WriteEndObject();
    }
}
