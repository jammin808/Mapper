using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace PlayMap.Net.Links;

/// <summary>A clip Stagewatch reports in its frame (its own playout source).</summary>
public sealed record StagewatchPlayout(string Title, double Remaining, double Duration, bool Running, string Source);

/// <summary>One Stagewatch state frame as PlayMap renders it (its <c>/ws</c> frame, the same JSON as its
/// /api/state). Durations, not wall clocks: <see cref="Seconds"/> is what was left when the frame was made (negative
/// past zero); the receiver counts on from the frame's arrival (<see cref="CountedOn"/>) and re-syncs on the next.</summary>
public sealed record StagewatchTimer
{
    public long Seq { get; init; }

    /// <summary>countdown, countup or tod.</summary>
    public string Mode { get; init; } = "countdown";

    /// <summary>idle, running, paused or expired.</summary>
    public string State { get; init; } = "idle";

    public double Seconds { get; init; }

    public double Total { get; init; }

    public string Text { get; init; } = "";

    /// <summary>normal, warning, wrapup or overtime.</summary>
    public string Phase { get; init; } = "normal";

    /// <summary>The cue ladder's colour for now, "#RRGGBB".</summary>
    public string Color { get; init; } = "";

    public string Title { get; init; } = "";

    public string Next { get; init; } = "";

    public string Message { get; init; } = "";

    /// <summary>info, warning or critical.</summary>
    public string Urgency { get; init; } = "";

    /// <summary>off, slow or fast.</summary>
    public string Flash { get; init; } = "off";

    public long MessageId { get; init; }

    public bool Blackout { get; init; }

    /// <summary>A holding slide's id ("" = none) and name.</summary>
    public string Holding { get; init; } = "";

    public string HoldingName { get; init; } = "";

    /// <summary>Must be shown whenever it is not empty.</summary>
    public string Watermark { get; init; } = "";

    /// <summary>The speaker's cue ("5 MIN").</summary>
    public string Cue { get; init; } = "";

    public string Style { get; init; } = "";

    public StagewatchPlayout? Playout { get; init; }

    /// <summary>Which elements the operator shows (timer, message, title, next, progress, playout…).</summary>
    public IReadOnlyDictionary<string, bool> Show { get; init; } = new Dictionary<string, bool>(StringComparer.Ordinal);

    public bool Counting => State == "running";

    public bool CountsDown => Mode is "countdown" or "";

    /// <summary>A frame, or null when the JSON is not a state frame (a beat, a pong, a reply).</summary>
    public static StagewatchTimer? Parse(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object)
            return null;
        if (JsonText.Str(r, "type") is { Length: > 0 } type && type != "state")
            return null;
        if (!r.TryGetProperty("seconds", out _) && !r.TryGetProperty("text", out _))
            return null;
        var po = JsonText.Obj(r, "playout");
        var show = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (JsonText.Obj(r, "show") is { ValueKind: JsonValueKind.Object } s)
            foreach (var p in s.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    show[p.Name] = p.Value.GetBoolean();
        return new StagewatchTimer
        {
            Seq = JsonText.Long(r, "seq"),
            Mode = JsonText.Str(r, "mode").ToLowerInvariant(),
            State = JsonText.Str(r, "state").ToLowerInvariant(),
            Seconds = JsonText.Num(r, "seconds"),
            Total = JsonText.Num(r, "total"),
            Text = JsonText.Str(r, "text"),
            Phase = JsonText.Str(r, "phase").ToLowerInvariant(),
            Color = JsonText.Str(r, "color"),
            Title = JsonText.Str(r, "title"),
            Next = JsonText.Str(r, "next"),
            Message = JsonText.Str(r, "message"),
            Urgency = JsonText.Str(r, "urgency"),
            Flash = JsonText.Str(r, "flash") is { Length: > 0 } f ? f : "off",
            MessageId = JsonText.Long(r, "messageId"),
            Blackout = JsonText.Bool(r, "blackout"),
            Holding = JsonText.Str(r, "holding"),
            HoldingName = JsonText.Str(r, "holdingName"),
            Watermark = JsonText.Str(r, "watermark"),
            Cue = JsonText.Str(r, "cue"),
            Style = JsonText.Str(r, "style"),
            Playout = po.ValueKind == JsonValueKind.Object
                ? new StagewatchPlayout(JsonText.Str(po, "title"), JsonText.Num(po, "remaining"), JsonText.Num(po, "duration"), JsonText.Bool(po, "running"), JsonText.Str(po, "source"))
                : null,
            Show = show,
        };
    }

    /// <summary>The frame counted on by <paramref name="elapsedSeconds"/> since it arrived: a running countdown
    /// counts down (into overtime past zero), a running count-up counts up; anything else is as the frame said.</summary>
    public StagewatchTimer CountedOn(double elapsedSeconds)
    {
        if (!Counting || elapsedSeconds <= 0 || Mode == "tod")
            return this;
        var seconds = CountsDown ? Seconds - elapsedSeconds : Seconds + elapsedSeconds;
        return this with { Seconds = seconds, Text = Format(seconds, CountsDown) };
    }

    /// <summary>"12:30", "1:02:03", "-00:12": whole seconds as Stagewatch writes them — a countdown rounds up while
    /// above zero (it says 0:01 until the second is over).</summary>
    public static string Format(double seconds, bool countdown = true)
    {
        var whole = (long)(countdown && seconds > 0 ? Math.Ceiling(seconds - 1e-9) : seconds < 0 ? Math.Ceiling(seconds - 1e-9) : Math.Floor(seconds));
        var sign = whole < 0 ? "-" : "";
        var a = Math.Abs(whole);
        var h = a / 3600;
        var m = a / 60 % 60;
        var s = a % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{m:00}:{s:00}");
    }
}

/// <summary>What a screen shows of the linked Stagewatch now. <see cref="Text"/> is "?" whenever the time is not a
/// fact — no frame yet, the link stale or down, or Stagewatch saying its own show is stuck: a frozen number on a
/// speaker's screen is worse than none. <see cref="Timer"/> is the last frame (counted on while the time is a fact).</summary>
public sealed record StagewatchReading(StagewatchTimer? Timer, bool Trusted, string Text, string Why)
{
    public static readonly StagewatchReading Unknown = new(null, false, "?", "no Stagewatch frame yet");

    /// <summary>The keys of STATE's <c>links.stagewatch.timer</c> in the order <see cref="ToStateJson"/> writes them. STATE is read
    /// as text by some readers, so a key added later goes after the last of these and none moves.</summary>
    public static readonly ImmutableArray<string> StateKeys =
        ["state", "text", "seconds", "phase", "color", "title", "message", "trusted", "why", "blackout", "watermark"];

    /// <summary>Stagewatch's blackout as this node follows it: the last frame's word, held while the time is not a fact (the link
    /// stale or down, or its show stuck), since a blackout is released only by Stagewatch's live word or a person, never by
    /// silence (Show Bus 1.3 §13.17). False before the first frame.</summary>
    public bool Blackout => Timer?.Blackout ?? false;

    /// <summary>The mark Stagewatch asks every picture of its timer to carry (its Free mode's): the last frame's, kept while the time
    /// is not a fact, so a link going quiet never takes it off the air; "" before the first frame.</summary>
    public string Watermark => Timer?.Watermark ?? "";

    /// <summary>STATE's <c>links.stagewatch.timer</c>, keys in <see cref="StateKeys"/>' order: the state and the seconds only while
    /// the time is a fact ("unknown" and null otherwise), the last frame's phase, colour, title and message, whether the time is
    /// trusted and why not, then the blackout and the watermark this node follows.</summary>
    public string ToStateJson() => JsonText.Write(w =>
    {
        w.WriteStartObject();
        var t = Timer;
        w.WriteString("state", Trusted ? t?.State ?? "idle" : "unknown");
        w.WriteString("text", Text);
        if (Trusted && t is not null)
            w.WriteNumber("seconds", Math.Round(t.Seconds, 1));
        else
            w.WriteNull("seconds");
        w.WriteString("phase", t?.Phase ?? "");
        w.WriteString("color", t?.Color ?? "");
        w.WriteString("title", t?.Title ?? "");
        w.WriteString("message", t?.Message ?? "");
        w.WriteBoolean("trusted", Trusted);
        w.WriteString("why", Why);
        w.WriteBoolean("blackout", Blackout);
        w.WriteString("watermark", Watermark);
        w.WriteEndObject();
    });
}

/// <summary>A screen command from Stagewatch's operator (its Screens page): redirect (show another view), identify
/// (Stagewatch's ask to show this screen's name, which this node journals and does not show), reload, disconnect.</summary>
public sealed record StagewatchScreenCommand(string Type, string To, string Name, string Id);
