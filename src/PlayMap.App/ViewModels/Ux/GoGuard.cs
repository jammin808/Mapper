using System.Globalization;
using PlayMap.App.Infrastructure;
using PlayMap.Core.Wire;

namespace PlayMap.App.ViewModels.Ux;

/// <summary>The GO guard made visible, on the desk's clock. A GO line (PLAYLIST GO, NEXT, PREV, VIDEO PLAY, VIDEO RESTART: the lines the
/// node's GO fence holds) that the node accepted opens its playlist's window for the node's MinGo (STATE.show.minGoMs) from the
/// reply, and a GO from any client opens it from STATE.show.lastGo for what is left of MinGo: while it is open every GO control of
/// that playlist draws its red border, and it closes when the window does; a later report of a GO never shortens an open window. A GO
/// the node refused with its double-GO words is one flash of <see cref="FlashMs"/> — a single event, and no flash starts within
/// <see cref="StatusShapes.MinAlarmPeriodMs"/> of the last one's start, so the guard never flashes faster than 2 Hz however fast the
/// refusals come — whose words the controls' tooltips and the attention bar carry until the window it hit has closed. A GO the node never
/// answered (unconfirmed: written, and no answer within the reply wait or its link dropped first) is neither: it opens no window and
/// flashes nothing, and its words — on which playlist, that it may have run, and to look at the node's state — stand, amber, in the
/// tooltips of that playlist's GO controls and in the attention bar (the latest, with how many more), each playlist's apart, until the
/// node's STATE shows a GO on its playlist made since it was sent (read before its reply settled or after) or a later GO of this desk's on
/// that playlist is answered. Nothing is ever sent again for it. The guard only shows the node's fence; the node decides.</summary>
public sealed class GoGuard
{
    /// <summary>The words the node's fence refuses a GO with (EngineCore.Transport's GoFence).</summary>
    public const string FenceWords = "double GO protection";

    /// <summary>A flash lasts this long; the next one starts no sooner than <see cref="StatusShapes.MinAlarmPeriodMs"/> after its start.</summary>
    public const long FlashMs = 333;

    private readonly Func<long> _nowMs;
    private readonly Dictionary<string, long> _openUntil = new(StringComparer.Ordinal);
    private long _flashAtMs;
    private bool _flashed;
    private bool _flashOn;
    private long _refusedUntilMs;
    private readonly Func<string, string?>? _playlistName;
    /// <summary>When (desk ms) this desk last sent a GO on each playlist ("" the focused one's): one time per playlist, the latest, so a GO
    /// still in flight when a later one on its playlist is sent is dated from the later send — its words stand longer, never shorter.</summary>
    private readonly Dictionary<string, long> _sentAtMs = new(StringComparer.Ordinal);
    /// <summary>When (desk ms) the latest GO the node's STATE showed on each playlist was made: the desk's time it was read less the GO's
    /// age at the node's push, so a GO made before a send and read after it is never taken for the node's state since that send.</summary>
    private readonly Dictionary<string, long> _seenAtMs = new(StringComparer.Ordinal);
    /// <summary>Each GO of this desk's the node never answered, by its playlist ("" a line naming none: the focused playlist's), until it
    /// is settled.</summary>
    private readonly Dictionary<string, Unanswered> _unconfirmed = new(StringComparer.Ordinal);
    private long _unconfirmedOrder;

    /// <summary>An unconfirmed GO's words, when it was sent (desk ms) and its place among them (the latest is the attention bar's).</summary>
    private readonly record struct Unanswered(string Words, long SentAtMs, long Order);

    /// <param name="playlistName">A playlist's name by its id (the node's STATE), for an unconfirmed GO's words; its id when null or
    /// unknown.</param>
    public GoGuard(Func<long> nowMs, Func<string, string?>? playlistName = null)
    {
        ArgumentNullException.ThrowIfNull(nowMs);
        _nowMs = nowMs;
        _playlistName = playlistName;
    }

    /// <summary>Raised when a window opened or closed, a flash began or ended, or the refusal's words came or went.</summary>
    public event Action? Changed;

    /// <summary>How many flashes there have been (a refused GO within 500 ms of the last flash's start adds none).</summary>
    public int Flashes { get; private set; }

    /// <summary>The node's words for the last refused GO, while the window it hit is open; null otherwise.</summary>
    public string? RefusedWords { get; private set; }

    /// <summary>The attention bar's words for the GOs of this desk's the node never answered: the latest one's ("GO unconfirmed on Show —
    /// " and why: it may have run; look at its state) and how many more stand; null when none stands.</summary>
    public string? UnconfirmedWords
    {
        get
        {
            if (_unconfirmed.Count == 0)
                return null;
            var latest = _unconfirmed.Values.MaxBy(u => u.Order).Words;
            return _unconfirmed.Count == 1 ? latest : latest + " · " + (_unconfirmed.Count - 1).ToString(CultureInfo.InvariantCulture) + " more GO unconfirmed";
        }
    }

    /// <summary>A GO control's words for its playlist (<see cref="GoPlaylist"/>'s): the unconfirmed GO on it, or else one whose line named
    /// no playlist (it may have been this one); for a line naming none, the attention bar's; null when none stands.</summary>
    public string? UnconfirmedWordsFor(string playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (playlist.Length == 0)
            return UnconfirmedWords;
        return _unconfirmed.TryGetValue(playlist, out var on) || _unconfirmed.TryGetValue("", out on) ? on.Words : null;
    }

    /// <summary>The desk sent <paramref name="line"/> at <paramref name="atMs"/> (the desk's clock, read as the line was handed to the
    /// link, not when this runs): a GO's send time, from which a GO the node's STATE shows on its playlist is read as the node's state
    /// since it.</summary>
    public void Sending(string line, long atMs)
    {
        if (GoPlaylist(line) is { } playlist)
            _sentAtMs[playlist] = atMs;
    }

    /// <summary>The GO lines' playlist ("" when the line names none: the focused playlist's), or null for a line that is no GO.</summary>
    public static string? GoPlaylist(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || !WireGrammar.TryParse(line, out var parsed, out _) || VerbCatalog.Match(parsed) is not { } m)
            return null;
        switch (m.Spec.Name)
        {
            case "PLAYLIST GO" or "PLAYLIST NEXT" or "PLAYLIST PREV":
                return parsed.Count > m.Words ? parsed.Word(m.Words) : "";
            case "VIDEO PLAY" or "VIDEO RESTART":
                for (var i = m.Words; i < parsed.Count - 1; i++)
                {
                    if (parsed.WordUpper(i) == "ON")
                        return parsed.Word(i + 1);
                }
                return "";
            default:
                return null;
        }
    }

    /// <summary>True while the window after an accepted GO on this line's playlist is open (a line naming no playlist: any window).</summary>
    public bool IsOpen(string? line)
    {
        if (GoPlaylist(line) is not { } playlist)
            return false;
        var now = _nowMs();
        return playlist.Length == 0 ? _openUntil.Values.Any(until => now < until) : _openUntil.TryGetValue(playlist, out var u) && now < u;
    }

    /// <summary>True during a flash (a GO line refused by the fence, for <see cref="FlashMs"/>).</summary>
    public bool IsFlashing(string? line) => GoPlaylist(line) is not null && IsFlashingNow;

    public bool IsFlashingNow
    {
        get
        {
            var now = _nowMs();
            return _flashOn && now - _flashAtMs < FlashMs;
        }
    }

    /// <summary>The desk sent a line and it settled: an applied or accepted GO opens its window for <paramref name="minGoMs"/>; one the
    /// fence refused flashes (at most one flash start per <see cref="StatusShapes.MinAlarmPeriodMs"/>) and its words stay while the window
    /// is open; one the node never answered (<see cref="WireReply.IsUnconfirmed"/>) is neither done nor refused — no window, no flash, an
    /// earlier refusal's words give way — and its words stand until a GO on its playlist made since it was sent is seen in STATE (one
    /// seen already leaves none) or a later GO on that playlist is answered. Either answer ends an earlier unconfirmed GO's words on the
    /// playlist the line names (on none: those of a line that named none), never another's.</summary>
    public void Replied(string line, WireReply reply, int minGoMs)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (GoPlaylist(line) is not { } playlist)
            return;
        var now = _nowMs();
        if (reply.IsUnconfirmed)
        {
            RefusedWords = null;
            var sent = _sentAtMs.TryGetValue(playlist, out var at) ? at : now;
            if (SeenSince(playlist, sent))
                _unconfirmed.Remove(playlist);
            else
                _unconfirmed[playlist] = new Unanswered(UnconfirmedOn(playlist, reply.Text), sent, ++_unconfirmedOrder);
            Changed?.Invoke();
            return;
        }
        if (reply.Ok)
        {
            var settled = _unconfirmed.Remove(playlist);
            if (minGoMs <= 0)
            {
                if (settled)
                    Changed?.Invoke();
                return;
            }
            _openUntil[playlist] = now + minGoMs;
            RefusedWords = null;
            Changed?.Invoke();
            return;
        }
        if (!reply.Text.Contains(FenceWords, StringComparison.OrdinalIgnoreCase))
            return;
        _unconfirmed.Remove(playlist);
        RefusedWords = Link.DeskSession.Plain(reply.Text);
        _refusedUntilMs = Math.Max(now + FlashMs, _openUntil.Values.DefaultIfEmpty(0).Max());
        if (!_flashed || now - _flashAtMs >= StatusShapes.MinAlarmPeriodMs)
        {
            _flashed = true;
            _flashAtMs = now;
            _flashOn = true;
            Flashes++;
        }
        Changed?.Invoke();
    }

    /// <summary>The node published a GO on <paramref name="playlist"/> (STATE.show.lastGo, from any client) <paramref name="agoMs"/>
    /// before it wrote the push that carries it, both on the node's clock: the playlist's window opens for what is left of
    /// <paramref name="minGoMs"/>, on the desk's clock from now. A GO the window has already passed opens nothing, and an open window
    /// (this desk's own GO, opened from its reply) is never shortened. A GO made on the playlist since an unconfirmed GO there (or on a
    /// line naming none) was sent ends that GO's words, whatever is left of its window: the node's state has answered.</summary>
    public void Published(string playlist, long agoMs, int minGoMs)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        var now = _nowMs();
        var seen = false;
        if (playlist.Length > 0)
        {
            // When the GO was made, on the desk's clock: never later than its own read, and for a GO of this desk's never before its send.
            var madeAt = now - Math.Max(0, agoMs);
            _seenAtMs[playlist] = _seenAtMs.TryGetValue(playlist, out var had) ? Math.Max(had, madeAt) : madeAt;
            foreach (var key in new[] { playlist, "" })
            {
                if (_unconfirmed.TryGetValue(key, out var u) && madeAt >= u.SentAtMs)
                    seen |= _unconfirmed.Remove(key);
            }
        }
        var left = minGoMs - Math.Max(0, agoMs);
        var until = now + left;
        if (playlist.Length == 0 || left <= 0 || (_openUntil.TryGetValue(playlist, out var open) && open >= until))
        {
            if (seen)
                Changed?.Invoke();
            return;
        }
        _openUntil[playlist] = until;
        Changed?.Invoke();
    }

    /// <summary>True when the node's STATE has shown a GO on <paramref name="playlist"/> (any, for a line naming none) made at or after
    /// <paramref name="sentAtMs"/>.</summary>
    private bool SeenSince(string playlist, long sentAtMs) =>
        _seenAtMs.Any(p => (playlist.Length == 0 || p.Key == playlist) && p.Value >= sentAtMs);

    /// <summary>"GO unconfirmed on Show — " (its playlist's name, or its id; none for a line naming none) and the link's reason, ending
    /// that it may have run and to look at its state.</summary>
    private string UnconfirmedOn(string playlist, string text)
    {
        var on = playlist.Length == 0 ? "" : " on " + (_playlistName?.Invoke(playlist) is { Length: > 0 } name ? name : playlist);
        return "GO unconfirmed" + on + " — " + Link.DeskSession.UnconfirmedReason(text);
    }

    /// <summary>When the guard next changes by itself (a window or a flash ending), in desk ms from now; null when nothing is due.</summary>
    public long? DueInMs
    {
        get
        {
            var now = _nowMs();
            var due = _openUntil.Values.Where(u => u > now).Append(_flashOn ? _flashAtMs + FlashMs : long.MaxValue)
                .Append(RefusedWords is not null && _refusedUntilMs > now ? _refusedUntilMs : long.MaxValue).Min();
            return due == long.MaxValue ? null : due - now;
        }
    }

    /// <summary>Closes what has run out on the desk's clock (the window's tick, or a wake at <see cref="DueInMs"/>).</summary>
    public void Tick()
    {
        var now = _nowMs();
        var moved = false;
        foreach (var (playlist, until) in _openUntil.ToList())
        {
            if (now >= until)
            {
                _openUntil.Remove(playlist);
                moved = true;
            }
        }
        if (_flashOn && now - _flashAtMs >= FlashMs)
        {
            _flashOn = false;
            moved = true;
        }
        if (RefusedWords is not null && now >= _refusedUntilMs)
        {
            RefusedWords = null;
            moved = true;
        }
        if (moved)
            Changed?.Invoke();
    }
}
