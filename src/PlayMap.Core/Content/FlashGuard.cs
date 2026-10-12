namespace PlayMap.Core.Content;

/// <summary>How often the picture may flash. A full-field light-to-dark-to-light change is the one visual
/// effect with a documented harm attached to it: broadcast and web guidance both draw the line at three such
/// changes a second, and a room full of people is exactly who a show draws for. A flash that comes too soon
/// after the last one is not shortened or dimmed, it is dropped, and the one after it lands. The peak is
/// capped too — a slow whole-screen white is still unpleasant on a wall.
///
/// One guard per picture that can flash (a generator instance); it holds no clock of its own — the leader
/// time is passed in — so a sequence of calls is a unit test rather than a wait.</summary>
public sealed class FlashGuard
{
    /// <summary>The line the guidance draws: three large-area luminance changes in any one second.</summary>
    public const int MaxFlashesPerSecond = 3;

    /// <summary>The most white a flash may put over the picture, whatever it asked for.</summary>
    public const float MaxLevel = 0.7f;

    /// <summary>Above this the screen counts as flashed.</summary>
    public const float RiseLevel = 0.10f;

    /// <summary>Below this the screen counts as dark again, so the next rise is a new flash. Under the rise
    /// level, with room between, so a flicker at the boundary is not counted twice.</summary>
    public const float FallLevel = 0.04f;

    /// <summary>The shortest gap between two flashes.</summary>
    public static readonly double MinPeriodSeconds = 1.0 / MaxFlashesPerSecond;

    /// <summary>The most a sound-reactive scene may swing the picture's brightness: enough to read as
    /// reactive, never enough to be a flash.</summary>
    public const float MaxAudioBrightnessSwing = 0.25f;

    private bool _lit;
    private bool _dropped;
    private double _lastRiseSeconds = double.NegativeInfinity;

    /// <summary>Flashes let through since this guard was made or reset.</summary>
    public int Allowed { get; private set; }

    /// <summary>Flashes dropped for coming too soon.</summary>
    public int Dropped { get; private set; }

    /// <summary>The flash this frame may actually draw: what was asked for, capped, or nothing when it is too
    /// soon after the last one. A dropped flash stays dropped for the whole of its pulse — letting it in
    /// halfway would be a shorter, sharper flash than the one refused.</summary>
    /// <param name="wanted">The flash asked for, 0–1.</param>
    /// <param name="seconds">The leader clock.</param>
    public float Limit(float wanted, double seconds)
    {
        RewindIfClockWentBack(seconds);

        if (float.IsNaN(wanted) || wanted <= FallLevel)
        {
            _lit = false;
            _dropped = false;
            return wanted > 0 && !float.IsNaN(wanted) ? Math.Min(wanted, MaxLevel) : 0f;
        }

        if (_lit)
            return _dropped ? 0f : Math.Min(wanted, MaxLevel);

        // Between the fall and the rise: on its way up, not yet a flash.
        if (wanted < RiseLevel)
            return _dropped ? 0f : Math.Min(wanted, MaxLevel);

        _lit = true;
        if (seconds - _lastRiseSeconds < MinPeriodSeconds)
        {
            _dropped = true;
            Dropped++;
            return 0f;
        }
        _dropped = false;
        _lastRiseSeconds = seconds;
        Allowed++;
        return Math.Min(wanted, MaxLevel);
    }

    /// <summary>A whole-screen light change that happens once (a colour step): true to draw it, false when
    /// it comes too soon after the last flash. It shares the budget with <see cref="Limit"/>.</summary>
    public bool AllowPulse(float wanted, double seconds)
    {
        RewindIfClockWentBack(seconds);
        if (float.IsNaN(wanted) || wanted < RiseLevel)
            return true;
        if (seconds - _lastRiseSeconds < MinPeriodSeconds)
        {
            Dropped++;
            return false;
        }
        _lastRiseSeconds = seconds;
        Allowed++;
        return true;
    }

    /// <summary>Forget the last flash (a generator rebuilt, the tests).</summary>
    public void Reset()
    {
        _lit = false;
        _dropped = false;
        _lastRiseSeconds = double.NegativeInfinity;
        Allowed = 0;
        Dropped = 0;
    }

    /// <summary>The brightness a scene may draw at for an audio level, bounded by
    /// <see cref="MaxAudioBrightnessSwing"/>.</summary>
    public static float Brightness(float baseline, float level, float amount)
    {
        var swing = Math.Clamp(amount, 0f, 1f) * MaxAudioBrightnessSwing;
        return Math.Clamp(baseline + (Math.Clamp(level, 0f, 1f) - 0.5f) * 2f * swing, 0.05f, 2f);
    }

    // A leader clock that went backwards (a seek, a test's own clock): start again rather than hold
    // everything down until the old time comes round.
    private void RewindIfClockWentBack(double seconds)
    {
        if (seconds >= _lastRiseSeconds)
            return;
        _lastRiseSeconds = double.NegativeInfinity;
        _lit = false;
        _dropped = false;
    }
}

/// <summary>The sync check: a white flash for <c>flash</c> seconds at every multiple of <c>period</c> on the
/// leader's clock. Film two walls (or watch the stream and the room together) and the gap between their
/// flashes is the offset to dial in. Pure: every node computes it from its own leader time.</summary>
public static class SyncMarks
{
    public const double DefaultPeriodSeconds = 2.0;
    public const double DefaultFlashSeconds = 0.05;

    /// <summary>True while a flash is up at this leader time.</summary>
    public static bool IsFlash(double leaderSeconds, double periodSeconds = DefaultPeriodSeconds, double flashSeconds = DefaultFlashSeconds) =>
        leaderSeconds >= 0 && periodSeconds > 0 && SinceMark(leaderSeconds, periodSeconds) < flashSeconds;

    /// <summary>Seconds since the mark at or before this leader time.</summary>
    public static double SinceMark(double leaderSeconds, double periodSeconds = DefaultPeriodSeconds)
    {
        if (periodSeconds <= 0)
            return 0;
        var t = Math.Max(0, leaderSeconds);
        return t - Math.Floor(t / periodSeconds) * periodSeconds;
    }

    /// <summary>The next mark strictly after this leader time.</summary>
    public static double NextMark(double leaderSeconds, double periodSeconds = DefaultPeriodSeconds) =>
        (Math.Floor(Math.Max(0, leaderSeconds) / periodSeconds) + 1) * periodSeconds;

    /// <summary>The mark this leader time belongs to (the one at or before it).</summary>
    public static double MarkBefore(double leaderSeconds, double periodSeconds = DefaultPeriodSeconds) =>
        Math.Floor(Math.Max(0, leaderSeconds) / periodSeconds) * periodSeconds;
}
