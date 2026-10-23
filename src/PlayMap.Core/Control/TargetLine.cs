using PlayMap.Core.Wire;

namespace PlayMap.Core.Control;

/// <summary>The word in a target line a control's value replaces: "*" the shaped value, "*%" a 0–100 integer, "#*" a 1-based index.</summary>
public enum Placeholder { None, Value, Percent, Index }

/// <summary>A binding's target: a wire line a control fires, read once. At most one placeholder, a whole word; the line names a
/// verb that changes something, and never a session, secret, approval or binding verb (a binding never stores a secret, never
/// speaks for a person, never binds a binding); a path it quotes reads as typed
/// (<see cref="VerbSyntax.DroppedBackslash(VerbSpec, WireLine, int)"/>).</summary>
public sealed record TargetLine(string Text, WireLine Line, VerbSpec Spec, int VerbWords, Placeholder Placeholder, int PlaceholderAt)
{
    private static readonly HashSet<string> Refused = new(StringComparer.Ordinal)
    {
        "HELLO", "NONCE", "AUTH", "AUTH-PROOF", "PING", "SUBSCRIBE", "STATE", "CONTEXT", "HELP", "VERBS", "PAIRING NEW", "APPROVE",
        "REJECT", "ASK", "CONTROL INJECT", "CONTROL FORWARD", "CONTROL PERSONALITY",
    };

    public bool HasValue => Placeholder != Placeholder.None;

    /// <summary>SET / NUDGE / GENERATOR SET: the entity id and field the line writes (null otherwise).</summary>
    public (string Id, string Path)? Field => Spec.Name switch
    {
        "SET" or "NUDGE" or "GENERATOR SET" when Line.Count >= VerbWords + 2 => (Line.Word(VerbWords), Line.Word(VerbWords + 1)),
        _ => null,
    };

    /// <summary>The line's ON and OFF forms when its verb takes ON|OFF and the line says ON, OFF or TOGGLE as a word after the verb's
    /// own (a momentary button presses ON and releases OFF).</summary>
    public (string On, string Off)? SwitchForms
    {
        get
        {
            if (!Spec.Syntax.Contains("ON|OFF", StringComparison.Ordinal))
                return null;
            for (var i = VerbWords; i < Line.Count; i++)
            {
                if (Line.WordUpper(i) is "ON" or "OFF" or "TOGGLE")
                    return (With(i, "ON"), With(i, "OFF"));
            }
            return null;
        }
    }

    /// <summary>True when the verb takes TOGGLE and the line says it: a toggle button sends the line as is (the node flips its own fact).</summary>
    public bool Toggles => Spec.Syntax.Contains("|TOGGLE", StringComparison.Ordinal)
        && Enumerable.Range(VerbWords, Math.Max(0, Line.Count - VerbWords)).Any(i => Line.WordUpper(i) == "TOGGLE");

    /// <summary>The line with the placeholder replaced by a value (the line itself when it has none).</summary>
    public string Fill(double value) => Placeholder == Placeholder.None ? Text : With(PlaceholderAt, BindingMath.Format(value, Placeholder));

    /// <summary>The line with one word replaced, quoted as the grammar needs.</summary>
    public string With(int index, string word)
    {
        var words = Line.Words.ToArray();
        words[index] = word;
        return WireGrammar.Join(words);
    }

    /// <summary>Reads a target line; null with the reason when it cannot be one.</summary>
    public static TargetLine? Parse(string text, out string why)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            why = "no target line";
            return null;
        }
        if (!WireGrammar.TryParse(text.Trim(), out var line, out var error))
        {
            why = "the target is not a line: " + (error?.Reason ?? "unreadable");
            return null;
        }
        if (line.RequestId is not null)
        {
            why = "a target line carries no request id";
            return null;
        }
        if (VerbCatalog.Match(line) is not { } m)
        {
            // Only a bare word is echoed: a quoted phrase or JSON may hold a line and its token, which no refusal repeats.
            var first = line.Word(0);
            why = string.Equals(WireGrammar.Quote(first), first, StringComparison.Ordinal)
                ? $"'{first}' is no verb — HELP lists the vocabulary"
                : "the target's first word is a quoted phrase or JSON, never a verb — HELP lists the vocabulary";
            return null;
        }
        var spec = m.Spec;
        if (Refused.Contains(spec.Name) || spec.Name.StartsWith("BIND ", StringComparison.Ordinal)
            || spec.Name.StartsWith("CREDENTIAL", StringComparison.Ordinal) || spec.SecretFrom is not null || spec.Internal)
        {
            why = $"{spec.Name} cannot be bound to a control (no session, secret, approval or binding verb is)";
            return null;
        }
        if (!spec.Mutates)
        {
            why = $"{spec.Name} changes nothing — a control fires lines that act";
            return null;
        }
        // The line fires later, rebuilt from the words read now and past the door: a path that lost its backslashes here would name
        // another file then, so it is refused as the door refuses it.
        if (VerbSyntax.DroppedBackslash(spec, line, m.Words) is { } dropped)
        {
            why = dropped;
            return null;
        }
        var placeholder = Placeholder.None;
        var at = -1;
        for (var i = m.Words; i < line.Count; i++)
        {
            var w = line.Words[i];
            var p = w switch { "*" => Placeholder.Value, "*%" => Placeholder.Percent, "#*" => Placeholder.Index, _ => Placeholder.None };
            if (p != Placeholder.None)
            {
                if (placeholder != Placeholder.None)
                {
                    why = "a target line carries one value word ('*', '*%' or '#*'), not two";
                    return null;
                }
                (placeholder, at) = (p, i);
            }
            else if (w.Contains('*', StringComparison.Ordinal) && (w.StartsWith('{') || w.StartsWith('[')))
            {
                why = "a value can ride only as a whole word, never inside JSON";
                return null;
            }
        }
        return new TargetLine(text.Trim(), line, spec, m.Words, placeholder, at);
    }
}
