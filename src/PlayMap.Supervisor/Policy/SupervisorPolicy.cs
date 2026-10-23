using System.Globalization;

namespace PlayMap.Supervisor.Policy;

/// <summary>The supervisor's numbers (docs/round3/design/installer.md §5). <see cref="FirstGoodFrame"/> is the time a device rebuild
/// takes to its first good frame as the Windows job records it; zero is unmeasured, and the budgets then stand on their floors.</summary>
public sealed record SupervisorPolicy(
    TimeSpan FirstBackoff,
    TimeSpan MaxBackoff,
    TimeSpan HealthyRun,
    TimeSpan StaleFloor,
    TimeSpan HealthSilent,
    int MaxRestarts,
    TimeSpan RestartWindow,
    TimeSpan StopGrace,
    TimeSpan FirstGoodFrame,
    TimeSpan UnmeasuredPhaseBudget)
{
    /// <summary>1 s doubling to 30 s, a run of a minute starting the ladder again; frozen after 3 s (or four first good frames);
    /// HEALTH silent 10 s; five restarts in ten minutes stop the trying; 5 s for a normal stop; a start or rebuild exempt for 30 s
    /// while the rebuild's first good frame is unmeasured.</summary>
    public static readonly SupervisorPolicy Default = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1),
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), 5, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5), TimeSpan.Zero,
        TimeSpan.FromSeconds(30));

    /// <summary>How long open outputs' shown frames may stand still while the GPU runs: the larger of the floor and four first good
    /// frames.</summary>
    public TimeSpan StaleTime => FirstGoodFrame * 4 > StaleFloor ? FirstGoodFrame * 4 : StaleFloor;

    /// <summary>How long a start or a rebuild is exempt from the freeze rule: four first good frames (never under the stale time),
    /// or <see cref="UnmeasuredPhaseBudget"/> while the first good frame is unmeasured.</summary>
    public TimeSpan PhaseBudget => FirstGoodFrame > TimeSpan.Zero ? StaleTime : UnmeasuredPhaseBudget;

    /// <summary>The budget's words for the status file.</summary>
    public string PhaseBudgetWords => FirstGoodFrame > TimeSpan.Zero
        ? string.Create(CultureInfo.InvariantCulture, $"{PhaseBudget.TotalSeconds:0.#} s (four first good frames of {FirstGoodFrame.TotalSeconds:0.###} s)")
        : string.Create(CultureInfo.InvariantCulture, $"{PhaseBudget.TotalSeconds:0.#} s (the rebuild's first good frame is unmeasured on a real node)");

    /// <summary>The delay before restart <paramref name="failures"/> (0 = the first after a run that was not healthy).</summary>
    public TimeSpan Backoff(int failures)
    {
        var ticks = FirstBackoff.Ticks;
        for (var i = 0; i < failures && ticks < MaxBackoff.Ticks; i++)
            ticks *= 2;
        return TimeSpan.FromTicks(Math.Min(ticks, MaxBackoff.Ticks));
    }
}
