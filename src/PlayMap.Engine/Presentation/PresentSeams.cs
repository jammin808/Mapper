using PlayMap.Core.Sync;
using PlayMap.Render.Sync;
using PlayMap.Render.Timing;

namespace PlayMap.Engine.Presentation;

/// <summary>The machine's present path for STATE <c>machine.present</c>: the API and adapter, the path outputs take, whether it is
/// experimental, why it is not what was asked, device resets, the time a frame spends presenting (95th percentile, the pacing
/// window's wait for the vblank included), and the context's dialect and windowing API.</summary>
public sealed record MachinePresentView(string Api, string Adapter, string Path, bool Experimental, string Why, long Resets,
    double PresentMsP95, string Dialect, string Context);

/// <summary>One output's presentation for STATE and OUTPUT STATS: its numbers (taken on the render thread) and whether a presenter
/// shows it now.</summary>
public sealed record OutputPresentView(string OutputId, PresentSnapshot Present);

/// <summary>What the output edge says about presentation, beside <see cref="IOutputBackend"/> (the render host implements both; a
/// test backend can too). Any thread; published numbers only.</summary>
public interface IPresentBackend
{
    /// <summary>Every output the renderer drew, with its presentation now.</summary>
    IReadOnlyList<OutputPresentView> Present();

    MachinePresentView Machine();

    /// <summary>Zeroes the named outputs' counters (all when null) on the render thread; answers the totals that were there.</summary>
    Task<IReadOnlyList<OutputPresentView>> ResetAsync(IReadOnlyCollection<string>? outputIds, CancellationToken ct);
}

/// <summary>One output's place in the frame lock for STATE: its state, the reason word of its last refusal or fall-out, the words,
/// and how often it fell out and joined again.</summary>
public sealed record LockView(string OutputId, LockState State, string Reason, string Words, long FellOut, long Rejoins)
{
    public string StateWord => FrameLockMachine.Word(State);

    public bool Locked => State is LockState.Framelocked or LockState.HouseLocked or LockState.Grouped or LockState.SoftLocked;
}

/// <summary>The frame lock as the engine drives it (the render host implements it; its machines run on the render thread). Verbs set
/// the intent from any thread; the machines observe it on the next frame.</summary>
public interface IFrameLockBackend
{
    /// <summary>The tier's name ("nvapi", "fake", "none") and why it has no board ("" when it works).</summary>
    string Driver { get; }

    string DriverWords { get; }

    /// <summary>The board as last read (at most a second old), or null: none.</summary>
    FrameLockBoard? Board { get; }

    /// <summary>The group's wish now.</summary>
    LockIntent Intent { get; }

    /// <summary>Outputs held out of the lock by SYNC LEAVE &lt;output&gt; while the group's wish applies.</summary>
    IReadOnlyCollection<string> HeldOut { get; }

    /// <summary>Every enabled output's lock now (by output id).</summary>
    IReadOnlyDictionary<string, LockView> Locks();

    /// <summary>Where the frame's show time came from: "count" (the board's count under the lock) or "clock".</summary>
    string TimeSource { get; }

    /// <summary>Joins every enabled output (a new wish, so a refused one is asked again).</summary>
    void Apply(uint swapGroup, uint barrier);

    /// <summary>The whole group leaves (the wish no longer applies).</summary>
    void LeaveAll();

    /// <summary>These outputs leave while the group's wish still applies.</summary>
    void Leave(IReadOnlyCollection<string> outputIds);

    /// <summary>These outputs (that fell out, or were held out) join again.</summary>
    void Rejoin(IReadOnlyCollection<string> outputIds);

    /// <summary>Completes once every named output's lock satisfies <paramref name="settled"/>, or the time is up; answers the locks then.</summary>
    Task<IReadOnlyDictionary<string, LockView>> ObserveAsync(IReadOnlyCollection<string> outputIds, Func<LockView, bool> settled,
        TimeSpan within, CancellationToken ct);

    /// <summary>The anchor this node's lock counter maps counts to show time with (a leader's own, or the one it adopted).</summary>
    LockAnchor? LocalAnchor();

    /// <summary>Adopts the group leader's anchor (a follower).</summary>
    void OfferAnchor(LockAnchor anchor);
}
