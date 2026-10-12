using System.Globalization;

namespace PlayMap.Core.Playback;

public enum TransportState
{
    /// <summary>Nothing loaded.</summary>
    Empty,
    /// <summary>Loaded and held at a point, ready to go (the first frame is up).</summary>
    Cued,
    Playing,
    Paused,
    /// <summary>Reached its out point and holds the last frame.</summary>
    Ended,
}

/// <summary>The outcome of a transport verb: the new transport, or the reason it was refused (a verb that
/// would change nothing is refused — Patterns' doctrine, so "VIDEO PLAY" on a playing clip says so).</summary>
public readonly record struct TransportResult(ClipTransport Transport, bool Ok, string Words);

/// <summary>One clip's transport as an immutable value — the semantics of Patterns' VIDEO verbs (round 86)
/// over a <see cref="TimelineAnchor"/>: PLAY, PAUSE, STOP (top and hold), RESTART, SEEK, END n (n seconds
/// before the out point). <see cref="Seq"/> rises on every discontinuity (load, seek, pause, resume, loop,
/// rate) so a follower re-syncs exactly when it must (Show Bus §4).</summary>
public sealed record ClipTransport(
    string Key,
    TransportState State,
    TimelineAnchor Anchor,
    long InNs,
    /// <summary>0 = unknown length.</summary>
    long OutNs,
    bool Loop,
    long Seq)
{
    public static readonly ClipTransport Empty = new("", TransportState.Empty, TimelineAnchor.Zero, 0, 0, false, 0);

    public bool HasLength => OutNs > InNs;

    public long LengthNs => HasLength ? OutNs - InNs : 0;

    /// <summary>The position at a leader instant, wrapped for a loop and clamped to the out point.</summary>
    public long PositionAt(long leaderNs)
    {
        if (State == TransportState.Empty)
            return 0;
        var m = Anchor.MediaAt(leaderNs);
        if (!HasLength)
            return Math.Max(InNs, m);
        if (Loop && m >= OutNs)
            return InNs + (m - InNs) % LengthNs;
        return Math.Clamp(m, InNs, OutNs);
    }

    /// <summary>The time left at a leader instant (null when unknown or looping).</summary>
    public long? RemainingAt(long leaderNs) =>
        !HasLength || Loop || State == TransportState.Empty ? null : Math.Max(0, OutNs - PositionAt(leaderNs));

    public static ClipTransport Load(string key, long inNs, long outNs, bool loop, long seq) =>
        new(key, TransportState.Cued, new TimelineAnchor(inNs, 0, 1, false), inNs, outNs, loop, seq + 1);

    /// <summary>Starts (or resumes) at a leader instant — a future instant schedules the start.</summary>
    public TransportResult Play(long atLeaderNs)
    {
        switch (State)
        {
            case TransportState.Empty:
                return Refuse("nothing is loaded");
            case TransportState.Playing:
                return Refuse("already playing");
            case TransportState.Ended:
                return Refuse("the clip has ended — RESTART plays it from the top");
        }
        var pos = PositionAt(atLeaderNs);
        return Accept(this with
        {
            State = TransportState.Playing,
            Anchor = new TimelineAnchor(pos, atLeaderNs, Anchor.Rate, true),
            Seq = Seq + 1,
        }, "playing");
    }

    public TransportResult Pause(long atLeaderNs) => State switch
    {
        TransportState.Playing => Accept(this with
        {
            State = TransportState.Paused,
            Anchor = new TimelineAnchor(PositionAt(atLeaderNs), atLeaderNs, Anchor.Rate, false),
            Seq = Seq + 1,
        }, "paused at " + Format(PositionAt(atLeaderNs))),
        TransportState.Paused => Refuse("already paused"),
        TransportState.Empty => Refuse("nothing is loaded"),
        _ => Refuse("not playing"),
    };

    /// <summary>Back to the in point and held (Patterns: VIDEO STOP is a seek to 0 and a pause).</summary>
    public TransportResult Stop(long atLeaderNs)
    {
        if (State == TransportState.Empty)
            return Refuse("nothing is loaded");
        if (State == TransportState.Cued && Anchor.MediaAt(atLeaderNs) == InNs)
            return Refuse("already stopped at the top");
        return Accept(this with
        {
            State = TransportState.Cued,
            Anchor = new TimelineAnchor(InNs, atLeaderNs, Anchor.Rate, false),
            Seq = Seq + 1,
        }, "stopped at the top");
    }

    public TransportResult Restart(long atLeaderNs)
    {
        if (State == TransportState.Empty)
            return Refuse("nothing is loaded");
        return Accept(this with
        {
            State = TransportState.Playing,
            Anchor = new TimelineAnchor(InNs, atLeaderNs, Anchor.Rate, true),
            Seq = Seq + 1,
        }, "playing from the top");
    }

    /// <summary>Moves to a position (clamped to the clip); keeps playing or holding as it was. An ended clip
    /// seeked back before its end holds there (paused) until PLAY.</summary>
    public TransportResult Seek(long mediaNs, long atLeaderNs)
    {
        if (State == TransportState.Empty)
            return Refuse("nothing is loaded");
        var target = HasLength ? Math.Clamp(mediaNs, InNs, OutNs) : Math.Max(InNs, mediaNs);
        var playing = State == TransportState.Playing;
        var state = State == TransportState.Ended ? TransportState.Paused : State;
        if (HasLength && target >= OutNs && !Loop)
            state = TransportState.Ended;
        return Accept(this with
        {
            State = state,
            Anchor = new TimelineAnchor(target, atLeaderNs, Anchor.Rate, playing && state == TransportState.Playing),
            Seq = Seq + 1,
        }, "at " + Format(target));
    }

    /// <summary>Patterns' VIDEO END n: to n seconds before the out point (refused while the length is unknown).</summary>
    public TransportResult EndIn(double seconds, long atLeaderNs)
    {
        if (State == TransportState.Empty)
            return Refuse("nothing is loaded");
        if (!HasLength)
            return Refuse("the clip's length is not known yet");
        if (seconds is < 0 or > 3600 || double.IsNaN(seconds))
            return Refuse("END takes 0 to 3600 seconds");
        var r = Seek(OutNs - (long)Math.Round(seconds * 1e9), atLeaderNs);
        return r.Ok ? r with { Words = string.Create(CultureInfo.InvariantCulture, $"{seconds:0.##} s from the end") } : r;
    }

    public TransportResult SetRate(double rate, long atLeaderNs)
    {
        if (rate is < 0.05 or > 20 || double.IsNaN(rate))
            return Refuse("rate is 0.05 to 20");
        if (Math.Abs(rate - Anchor.Rate) < 1e-9)
            return Refuse("already at that rate");
        return Accept(this with { Anchor = Anchor.WithRate(rate, atLeaderNs), Seq = Seq + 1 },
            string.Create(CultureInfo.InvariantCulture, $"rate {rate:0.###}"));
    }

    /// <summary>Advances the state at a leader instant: a non-looping clip that passed its out point ends
    /// (and holds its last frame); a looping one bumps Seq once per wrap so followers re-anchor.</summary>
    public ClipTransport Tick(long leaderNs)
    {
        if (State != TransportState.Playing || !HasLength)
            return this;
        var m = Anchor.MediaAt(leaderNs);
        if (m < OutNs)
            return this;
        if (!Loop)
            return this with { State = TransportState.Ended, Anchor = new TimelineAnchor(OutNs, leaderNs, Anchor.Rate, false), Seq = Seq + 1 };
        var wrapped = InNs + (m - InNs) % LengthNs;
        return this with { Anchor = new TimelineAnchor(wrapped, leaderNs, Anchor.Rate, true), Seq = Seq + 1 };
    }

    private TransportResult Accept(ClipTransport next, string words) => new(next, true, words);

    private TransportResult Refuse(string why) => new(this, false, why);

    /// <summary>"1:02:03.5" / "1:23.25" / "0:07".</summary>
    public static string Format(long ns)
    {
        var total = ns / 1e9;
        var h = (long)(total / 3600);
        var m = (long)(total / 60 % 60);
        var s = total % 60;
        var sec = s.ToString(Math.Abs(s - Math.Round(s)) < 0.005 ? "00" : "00.##", CultureInfo.InvariantCulture);
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{sec}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{sec}");
    }

    /// <summary>Parses a seek point the way Patterns does: "83", "83.5", "2.5s", "1:23", "1:02:03", "top".
    /// "END" forms are the caller's (they need the length). Returns false for anything else.</summary>
    public static bool TryParsePoint(string? text, out long ns)
    {
        ns = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim();
        if (t.Equals("top", StringComparison.OrdinalIgnoreCase) || t.Equals("start", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.EndsWith('s') || t.EndsWith('S'))
            t = t[..^1];
        var parts = t.Split(':');
        if (parts.Length > 3)
            return false;
        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) || v < 0)
                return false;
            if (!last && Math.Abs(v - Math.Floor(v)) > 0)
                return false;
            if (i > 0 && v >= 60)
                return false;
            total = total * 60 + v;
        }
        if (total > 86400)
            return false;
        ns = (long)Math.Round(total * 1e9);
        return true;
    }
}
