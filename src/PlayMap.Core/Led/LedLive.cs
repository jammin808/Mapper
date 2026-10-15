using System.Collections.Immutable;

namespace PlayMap.Core.Led;

/// <summary>The LED test patterns: evaluated in address space, in code values (after the output gamma), the same by the patch
/// shader and <see cref="LedTestPatterns"/>.</summary>
public enum LedTestKind { None, Chase, Order, Universe, Fixture, Gradient, Walk, White, Full, Black }

/// <summary>The rig's live air (machine-local, journalled, never an undo step): the master level and its fade, the test pattern and
/// the fixtures it covers (null = every fixture), and the fixture being located until a leader instant.</summary>
public sealed record LedLive(double LevelFrom, double LevelTo, long FadeStartNs, long FadeLengthNs, LedTestKind Test,
    ImmutableHashSet<string>? TestScope, string TestWords, string? Locate, long LocateUntilNs)
{
    public static readonly LedLive Initial = new(1, 1, 0, 0, LedTestKind.None, null, "", null, 0);

    /// <summary>The master level at a leader instant (a fade is linear from its start).</summary>
    public double LevelAt(long leaderNs)
    {
        if (FadeLengthNs <= 0 || leaderNs >= FadeStartNs + FadeLengthNs)
            return LevelTo;
        if (leaderNs <= FadeStartNs)
            return LevelFrom;
        return LevelFrom + (LevelTo - LevelFrom) * ((double)(leaderNs - FadeStartNs) / FadeLengthNs);
    }

    public bool Locating(long leaderNs) => Locate is not null && leaderNs < LocateUntilNs;
}

/// <summary>Test pattern functions. Every pattern is a pure function of the slot's place (pixel in its fixture, the fixture, the row)
/// and a step on the leader-time grid (<see cref="StepsPerSecond"/> a second), so two nodes show the same pattern at the same
/// instant; none changes a pixel more often than once a second (a chase over fewer than ten pixels waits on the dark ones, a walk
/// moves once a second), well inside three flashes a second.</summary>
public static class LedTestPatterns
{
    public const int StepsPerSecond = 10;

    /// <summary>The step of a leader instant: floor(seconds × 10), wrapped to stay a non-negative int.</summary>
    public static int StepAt(long leaderNs) => (int)((FloorDiv(leaderNs, 1_000_000_000 / StepsPerSecond) % StepWrap + StepWrap) % StepWrap);

    /// <summary>Steps wrap here (a whole number of every pattern's period up to 1000 pixels a fixture), so a step is never negative.</summary>
    private const long StepWrap = 864_000_000;

    /// <summary>The locate blink's phase at a leader instant: white for a quarter second, then the fixture's editor colour (2 Hz).</summary>
    public static bool LocateWhite(long leaderNs) => (FloorDiv(leaderNs, 250_000_000) & 1) == 0;

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    private static readonly (float R, float G, float B)[] Palette =
        [(1, 0, 0), (0, 1, 0), (0, 0, 1), (1, 1, 0), (0, 1, 1), (1, 0, 1)];

    /// <summary>A pattern's code value for a pixel. <see cref="LedTestKind.Full"/> answers white; the patch pass sets every colour
    /// channel of the pixel full for it.</summary>
    public static (float R, float G, float B) Code(LedTestKind kind, int pixel, int pixels, int fixture, int fixtures, int row, int step)
    {
        switch (kind)
        {
            case LedTestKind.Chase:
                return pixel == Mod(step, Math.Max(pixels, StepsPerSecond)) ? (1, 1, 1) : (0, 0, 0);
            case LedTestKind.Order:
                return pixel switch { 0 => (1, 0, 0), 1 => (0, 1, 0), 2 => (0, 0, 1), _ => (0, 0, 0) };
            case LedTestKind.Universe:
                return Palette[Mod(row, Palette.Length)];
            case LedTestKind.Fixture:
                return Palette[Mod(fixture, Palette.Length)];
            case LedTestKind.Gradient:
                {
                    var level = pixel * 255 / Math.Max(pixels - 1, 1);
                    var v = level / 255f;
                    return (v, v, v);
                }
            case LedTestKind.Walk:
                return fixture == Mod(step / StepsPerSecond, Math.Max(fixtures, 1)) ? (1, 1, 1) : (0, 0, 0);
            case LedTestKind.White:
            case LedTestKind.Full:
                return (1, 1, 1);
            default:
                return (0, 0, 0);
        }
    }

    private static int Mod(int a, int n) => ((a % n) + n) % n;

    /// <summary>The verb's word for a pattern.</summary>
    public static string Word(LedTestKind kind) => kind.ToString().ToLowerInvariant();
}

/// <summary>Per fixture, what the frame's air does to it: four floats — the air's gain (a code multiplier: the output's gain for an
/// output fixture, else the darkest output's), the test pattern covers it (1/0), blackout reaches it (1/0), and the locate blink
/// (0 none, 1 white, 2 its editor colour). The render pass and the CPU reference fill it the same way.</summary>
public static class LedAir
{
    public static void Fill(LedPlan plan, LedLive live, long leaderNs, bool blackout, Func<string, float> outputGain, float rigGain, Span<float> into)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(outputGain);
        var locating = live.Locating(leaderNs);
        var white = LedTestPatterns.LocateWhite(leaderNs);
        foreach (var f in plan.Fixtures)
        {
            var o = f.Index * 4;
            into[o] = f.AirOutput.Length > 0 ? outputGain(f.AirOutput) : rigGain;
            into[o + 1] = live.Test != LedTestKind.None && (live.TestScope is null || live.TestScope.Contains(f.Id)) ? 1 : 0;
            into[o + 2] = blackout && f.FollowBlackout ? 1 : 0;
            into[o + 3] = locating && string.Equals(live.Locate, f.Id, StringComparison.Ordinal) ? (white ? 1 : 2) : 0;
        }
    }
}
