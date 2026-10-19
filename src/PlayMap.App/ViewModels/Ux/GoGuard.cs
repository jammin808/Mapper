using PlayMap.Core.Wire;

namespace PlayMap.App.ViewModels.Ux;

/// <summary>The GO guard made visible, on the desk's clock. A GO line (PLAYLIST GO, NEXT, PREV, VIDEO PLAY, VIDEO RESTART: the lines the
/// node's GO fence holds) that the node accepted opens its playlist's window for the node's MinGo (STATE.show.minGoMs) from the
/// reply: while it is open every GO control of that playlist draws its red border, and it closes when the window does. A GO the node
/// refused with its double-GO words is one flash — a single event, never a second within <see cref="FlashMs"/> — whose words the
/// controls' tooltips and the attention bar carry until the window it hit has closed. The guard only shows the node's fence; the
/// node decides.</summary>
public sealed class GoGuard
{
    /// <summary>The words the node's fence refuses a GO with (EngineCore.Transport's GoFence).</summary>
    public const string FenceWords = "double GO protection";

    /// <summary>A flash lasts this long, and no second one starts within it.</summary>
    public const long FlashMs = 333;

    private readonly Func<long> _nowMs;
    private readonly Dictionary<string, long> _openUntil = new(StringComparer.Ordinal);
    private long _flashAtMs;
    private bool _flashOn;
    private long _refusedUntilMs;

    public GoGuard(Func<long> nowMs)
    {
        ArgumentNullException.ThrowIfNull(nowMs);
        _nowMs = nowMs;
    }

    /// <summary>Raised when a window opened or closed, a flash began or ended, or the refusal's words came or went.</summary>
    public event Action? Changed;

    /// <summary>How many flashes there have been (a refused GO inside a flash adds none).</summary>
    public int Flashes { get; private set; }

    /// <summary>The node's words for the last refused GO, while the window it hit is open; null otherwise.</summary>
    public string? RefusedWords { get; private set; }

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

    /// <summary>The desk sent a line and the node answered it: an accepted GO opens its window for <paramref name="minGoMs"/>; one the
    /// fence refused flashes (once per <see cref="FlashMs"/>) and its words stay while the window is open.</summary>
    public void Replied(string line, bool ok, string text, int minGoMs)
    {
        if (GoPlaylist(line) is not { } playlist)
            return;
        var now = _nowMs();
        if (ok)
        {
            if (minGoMs <= 0)
                return;
            _openUntil[playlist] = now + minGoMs;
            RefusedWords = null;
            Changed?.Invoke();
            return;
        }
        if (!text.Contains(FenceWords, StringComparison.OrdinalIgnoreCase))
            return;
        RefusedWords = Link.DeskSession.Plain(text);
        _refusedUntilMs = Math.Max(now + FlashMs, _openUntil.Values.DefaultIfEmpty(0).Max());
        if (!IsFlashingNow)
        {
            _flashAtMs = now;
            _flashOn = true;
            Flashes++;
        }
        Changed?.Invoke();
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
