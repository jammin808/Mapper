using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>wp3-desk-ux's hooks into the engine (EngineCore.Modules.cs): the Oops history. UNDO LIST reads the undo steps of the store
/// this line's edits go to (the blind owner's draft, else the live project) newest first; UNDO TO takes a step and every newer one
/// back as one action through <see cref="ProjectStore.UndoTo"/>, so the store's guard (LockGuard) decides it as it decides UNDO and
/// the show's gate treats it as UNDO.</summary>
public sealed partial class EngineCore
{
    partial void RouteUndo(VerbRoutes routes)
    {
        routes.Lane("UNDO LIST", UndoList);
        routes.Lane("UNDO TO", UndoTo);
    }

    /// <summary>UNDO LIST [&lt;n 1-100&gt;]: the newest n steps (20 unless given) of this line's store, each with its number, the edit's
    /// words, who made it and when, and whether it is the draft's.</summary>
    private CommandReply UndoList(VerbCall c)
    {
        const string Syntax = "UNDO LIST [<n 1-100>]";
        var n = 20;
        if (c.Args.Count > 1
            || (c.Args.Count == 1 && (!int.TryParse(c.Args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n is < 1 or > 100)))
            return CommandReply.Fail(Syntax);
        // A query is no document edit, so the gate hands it no store: the blind owner's list is its draft's all the same.
        var store = _lineStore ?? DraftStoreFor(c.Session) ?? Store;
        var draft = !ReferenceEquals(store, Store);
        var steps = store.Steps;
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("draft", draft);
            w.WriteNumber("count", steps.Length);
            w.WriteBoolean("canRedo", store.CanRedo);
            w.WriteStartArray("steps");
            for (var i = 0; i < Math.Min(n, steps.Length); i++)
            {
                var s = steps[i];
                w.WriteStartObject();
                w.WriteNumber("step", s.Number);
                w.WriteString("label", s.Label);
                w.WriteString("who", s.Who);
                w.WriteNumber("at", s.AtUnixMs);
                w.WriteString("words", UndoStepWords(s));
                w.WriteBoolean("next", i == 0);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    /// <summary>UNDO TO &lt;step&gt;: the step and every newer one undone as one action (one revision, one journal row). Refused with the
    /// store's words — "nothing to undo", the range for an unknown step, LockGuard's across a lock — and nothing changes.</summary>
    private CommandReply UndoTo(VerbCall c)
    {
        if (c.Args.Count != 1 || !long.TryParse(c.Args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var step))
            return CommandReply.Fail("UNDO TO <step> — UNDO LIST names the steps");
        return FromEdit(Edits.UndoTo(step));
    }

    /// <summary>"set output Left width — Desk at 20:31:05" (the time left out when the step has no wall clock).</summary>
    private static string UndoStepWords(UndoStep s)
    {
        var who = s.Who.Length > 0 ? " — " + s.Who : "";
        var at = s.AtUnixMs > 0 ? (who.Length > 0 ? " at " : " — at ") + Seconds(s.AtUnixMs) : "";
        return s.Label + who + at;
    }
}
