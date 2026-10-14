namespace PlayMap.Core.Sync;

/// <summary>An output's place in the hardware frame lock. Each locked state needs its evidence (the board synced, the barrier
/// bound, the frame count advancing one a refresh); "free" states present on their own vblank, measured or not.</summary>
public enum LockState
{
    /// <summary>Free-running, and its vblanks are not measured (composed, offscreen, or the fit has not settled).</summary>
    FreeUnknown,
    /// <summary>Free-running on its own vblank, measured (the output's vblank clock has settled).</summary>
    FreeMeasured,
    /// <summary>In a swap group inside this node, with no barrier to other nodes.</summary>
    Grouped,
    /// <summary>A software genlock (a vendor's own), its vblanks aligned.</summary>
    SoftLocked,
    /// <summary>The board's framelock across nodes: synced, signal present, barrier bound, the count advancing.</summary>
    Framelocked,
    /// <summary>As framelocked, with the board genlocked to house sync.</summary>
    HouseLocked,
    /// <summary>Was locked (or joining); the lock broke and the output presents free-running. The reason is kept.</summary>
    FellOut,
    /// <summary>Asked to join; evidence accumulating (at most 5 s, then locked or fell out).</summary>
    Joining,
}

/// <summary>What the caller does after a step: the driver calls, in this order (leave before join), and whether to raise an alarm.</summary>
[Flags]
public enum LockActions
{
    None = 0,
    Join = 1,
    Bind = 2,
    Unbind = 4,
    Leave = 8,
    OverridePresent = 16,
    RestorePresent = 32,
    Alarm = 64,
}

/// <summary>The group's wish: join the hardware lock (<see cref="Apply"/>) in this swap group and barrier. <see cref="Seq"/> moves
/// with every new wish, so a refused one is not retried every frame.</summary>
public readonly record struct LockIntent(bool Apply, uint SwapGroup, uint Barrier, long Seq)
{
    public static readonly LockIntent None = new(false, 0, 0, 0);
}

/// <summary>One output's facts at one instant, gathered by the render thread once a frame (the board's once a second): the clock,
/// the board, the swap group, the 24-bit frame count, the present's health, the show, and the group's intent. The machine has no
/// clock of its own: <see cref="MonoNs"/> is its time.</summary>
public readonly record struct LockFacts(
    long MonoNs,
    double PeriodNs,
    bool Settled,
    bool BoardPresent,
    bool BoardConfigured,
    bool BoardSynced,
    bool SignalAvailable,
    bool HouseSync,
    bool CableIntoSwitch,
    bool Joined,
    bool BarrierBound,
    bool CountValid,
    uint Count24,
    bool WaitTimedOut,
    bool PresentDeviceBusy,
    bool DisplayChanged,
    bool Armed,
    bool BlackedOut,
    bool AtBoundary,
    LockIntent Intent)
{
    /// <summary>A person asked this output to join again (SYNC REJOIN).</summary>
    public bool RejoinAsked { get; init; }

    /// <summary>The node may rejoin on its own (NodeSettings.Present.FrameLock "auto"); "off" never joins.</summary>
    public bool AutoRejoin { get; init; } = true;

    /// <summary>The driver refused the join the last step asked for, with <see cref="JoinWords"/>.</summary>
    public bool JoinRefused { get; init; }

    public string? JoinWords { get; init; }
}

/// <summary>Why an output is not locked, as a stable word a test or a controller can act on.</summary>
public static class LockReasons
{
    public const string None = "";
    public const string NoBoard = "no-board";
    public const string NotConfigured = "not-configured";
    public const string CableIntoSwitch = "cable-into-switch";
    public const string Armed = "armed";
    public const string Stall = "stall";
    public const string CountStalled = "count-stalled";
    public const string BoardUnsynced = "board-unsynced";
    public const string DeviceBusy = "device-busy";
    public const string DisplayChanged = "display-changed";
    public const string JoinTimeout = "join-timeout";
    public const string JoinRefused = "join-refused";
}

/// <summary>One output's lock machine (pure): facts in, a state, words and the driver actions out.
///
/// <list type="number">
/// <item><b>Join.</b> The intent applies and the preconditions hold (a board, configured, no sync cable into a switch, not armed —
/// or armed and blacked out) → Join, Bind, OverridePresent; <see cref="LockState.Joining"/>. A refused wish says why and is not
/// retried until a new one (<see cref="LockIntent.Seq"/>).</item>
/// <item><b>Confirm.</b> Joining, 120 refreshes in a row with the count advancing by exactly one (mod 2²⁴), the board synced and the
/// barrier bound → framelocked (house-locked on house sync). Not within 5 s → fell out.</item>
/// <item><b>Fall out.</b> Joining or locked, and the present's wait timed out (the barrier stalled), the count stood still for 3
/// periods, the board was unsynced for 3 periods (locked only: a short loss is debounced), the device was busy, or the display
/// changed → Unbind, Leave, RestorePresent, Alarm; <see cref="LockState.FellOut"/> with the reason. The output keeps presenting
/// on its own vblank: nothing here ever waits.</item>
/// <item><b>Rejoin.</b> Fell out and armed → stays (never on its own while armed; SYNC REJOIN only while blacked out). Disarmed,
/// the board synced for the 10 s since the fall and the show at a boundary (blackout, or nothing playing) → joins again by itself
/// (auto). Otherwise only SYNC REJOIN (or a new SYNC APPLY).</item>
/// <item><b>Release.</b> The intent no longer applies → Unbind, Leave, RestorePresent; free.</item>
/// </list></summary>
public sealed class FrameLockMachine
{
    public const int ConfirmRefreshes = 120;
    public const double StallPeriods = 3;
    public const long JoinTimeoutNs = 5_000_000_000;
    public const long RejoinSyncedNs = 10_000_000_000;
    private const uint Mask24 = 0xFFFFFF;

    private bool _hasCount;
    private uint _lastCount;
    private long _lastAdvanceNs;
    private int _good;
    private long _unsyncedSinceNs = -1;
    private long _syncedSinceNs = -1;
    private long _joinStartedNs;
    private long _refusedSeq = long.MinValue;
    private long _joinedSeq = long.MinValue;
    private long _fellOutAtNs;
    private long _lastStepNs;

    public LockState State { get; private set; } = LockState.FreeUnknown;

    /// <summary>What the state means now, in words ("" when free and nothing was asked).</summary>
    public string Words { get; private set; } = "";

    /// <summary>The reason word of the last refusal or fall-out (<see cref="LockReasons"/>); "" when locked or free by choice.</summary>
    public string Reason { get; private set; } = LockReasons.None;

    /// <summary>Times this output fell out of the lock.</summary>
    public long FellOutCount { get; private set; }

    /// <summary>Times it joined again after falling out.</summary>
    public long Rejoins { get; private set; }

    /// <summary>When the current lock was confirmed (−1 when not locked).</summary>
    public long LockedSinceNs { get; private set; } = -1;

    /// <summary>Refreshes in a row the count advanced by exactly one.</summary>
    public int GoodRefreshes => _good;

    public bool IsLocked => State is LockState.Framelocked or LockState.HouseLocked or LockState.Grouped or LockState.SoftLocked;

    /// <summary>One step: the facts of now in, the actions for the caller to perform out.</summary>
    public LockActions Step(in LockFacts f)
    {
        _lastStepNs = f.MonoNs;
        Track(in f);
        switch (State)
        {
            case LockState.FreeUnknown or LockState.FreeMeasured:
                return Free(in f);
            case LockState.Joining:
                return Joining(in f);
            case LockState.FellOut:
                return FellOut(in f);
            default:
                return Locked(in f);
        }
    }

    private void Track(in LockFacts f)
    {
        if (f.CountValid)
        {
            if (!_hasCount)
            {
                _hasCount = true;
                _lastCount = f.Count24 & Mask24;
                _lastAdvanceNs = f.MonoNs;
            }
            else
            {
                var delta = (f.Count24 - _lastCount) & Mask24;
                if (delta == 1)
                {
                    _good++;
                    _lastAdvanceNs = f.MonoNs;
                }
                else if (delta != 0)
                {
                    // A count that jumped is no lock evidence: the run of good refreshes starts again.
                    _good = 0;
                    _lastAdvanceNs = f.MonoNs;
                }
                _lastCount = f.Count24 & Mask24;
            }
        }
        else
        {
            _hasCount = false;
            _good = 0;
        }
        if (f.BoardSynced)
        {
            _unsyncedSinceNs = -1;
            if (_syncedSinceNs < 0)
                _syncedSinceNs = f.MonoNs;
        }
        else
        {
            _syncedSinceNs = -1;
            if (_unsyncedSinceNs < 0)
                _unsyncedSinceNs = f.MonoNs;
        }
    }

    private LockActions Free(in LockFacts f)
    {
        if (!f.Intent.Apply || f.Intent.Seq == _refusedSeq)
        {
            State = f.Settled ? LockState.FreeMeasured : LockState.FreeUnknown;
            if (!f.Intent.Apply)
            {
                Words = "";
                Reason = LockReasons.None;
            }
            return LockActions.None;
        }
        if (!Precondition(in f, out var reason, out var words))
        {
            _refusedSeq = f.Intent.Seq;
            Reason = reason;
            Words = words;
            State = f.Settled ? LockState.FreeMeasured : LockState.FreeUnknown;
            return LockActions.None;
        }
        return StartJoin(in f);
    }

    private LockActions StartJoin(in LockFacts f)
    {
        State = LockState.Joining;
        _joinedSeq = f.Intent.Seq;
        _joinStartedNs = f.MonoNs;
        _good = 0;
        Reason = LockReasons.None;
        Words = $"joining swap group {f.Intent.SwapGroup}, barrier {f.Intent.Barrier}";
        return LockActions.Join | LockActions.Bind | LockActions.OverridePresent;
    }

    private LockActions Joining(in LockFacts f)
    {
        if (!f.Intent.Apply)
            return Release(in f);
        if (f.JoinRefused)
            return Fall(LockReasons.JoinRefused, $"the driver did not join: {f.JoinWords} — presenting free");
        if (Breaks(in f, joining: true, out var reason, out var words))
            return Fall(reason, words);
        if (_good >= ConfirmRefreshes && f.BoardSynced && f.BarrierBound)
        {
            State = f.HouseSync ? LockState.HouseLocked : LockState.Framelocked;
            LockedSinceNs = f.MonoNs;
            Reason = LockReasons.None;
            Words = f.HouseSync
                ? $"house-locked: swap group {f.Intent.SwapGroup}, barrier {f.Intent.Barrier}"
                : $"framelocked: swap group {f.Intent.SwapGroup}, barrier {f.Intent.Barrier}";
            return LockActions.None;
        }
        if (f.MonoNs - _joinStartedNs > JoinTimeoutNs)
            return Fall(LockReasons.JoinTimeout,
                $"did not lock within 5 s ({_good} of {ConfirmRefreshes} refreshes counted{(f.BoardSynced ? "" : ", the board is not synced")}) — presenting free");
        return LockActions.None;
    }

    private LockActions Locked(in LockFacts f)
    {
        if (!f.Intent.Apply)
            return Release(in f);
        if (Breaks(in f, joining: false, out var reason, out var words))
            return Fall(reason, words);
        return LockActions.None;
    }

    private LockActions FellOut(in LockFacts f)
    {
        if (!f.Intent.Apply)
        {
            State = f.Settled ? LockState.FreeMeasured : LockState.FreeUnknown;
            Words = "";
            Reason = LockReasons.None;
            return LockActions.None;
        }
        // SYNC REJOIN, or a new SYNC APPLY, asks again; a refusal says why and waits for the next ask.
        if (f.RejoinAsked || f.Intent.Seq != _joinedSeq)
        {
            _joinedSeq = f.Intent.Seq;
            if (!Precondition(in f, out var reason, out var words))
            {
                Words = words;
                Reason = reason;
                return LockActions.None;
            }
            Rejoins++;
            return StartJoin(in f);
        }
        // Never on its own while armed: a lock that comes back mid-show may step the picture.
        if (f.Armed || !f.AutoRejoin)
            return LockActions.None;
        // Ten seconds of a synced board since the fall, so a lock that keeps breaking does not flap in and out.
        if (_syncedSinceNs >= 0 && f.MonoNs - Math.Max(_syncedSinceNs, _fellOutAtNs) >= RejoinSyncedNs && f.AtBoundary
            && Precondition(in f, out _, out _))
        {
            Rejoins++;
            return StartJoin(in f);
        }
        return LockActions.None;
    }

    private LockActions Release(in LockFacts f)
    {
        State = f.Settled ? LockState.FreeMeasured : LockState.FreeUnknown;
        LockedSinceNs = -1;
        Words = "left the lock: presenting free";
        Reason = LockReasons.None;
        return LockActions.Unbind | LockActions.Leave | LockActions.RestorePresent;
    }

    private LockActions Fall(string reason, string words)
    {
        _fellOutAtNs = _lastStepNs;
        State = LockState.FellOut;
        LockedSinceNs = -1;
        FellOutCount++;
        Reason = reason;
        Words = words;
        _good = 0;
        return LockActions.Unbind | LockActions.Leave | LockActions.RestorePresent | LockActions.Alarm;
    }

    /// <summary>What breaks a lock (or a join): a present whose wait timed out, a count that stood still for 3 periods, a busy device,
    /// a changed display; and, once locked, a board unsynced for 3 periods (a join waits for the board up to its 5 s).</summary>
    private bool Breaks(in LockFacts f, bool joining, out string reason, out string words)
    {
        var stallNs = StallPeriods * (f.PeriodNs > 0 ? f.PeriodNs : 1e9 / 60);
        if (f.WaitTimedOut)
        {
            (reason, words) = (LockReasons.Stall, "the barrier stalled 3 frames — presenting free; SYNC REJOIN when ready");
            return true;
        }
        if (f.PresentDeviceBusy)
        {
            (reason, words) = (LockReasons.DeviceBusy, "the device was busy at a present — presenting free; SYNC REJOIN when ready");
            return true;
        }
        if (f.DisplayChanged)
        {
            (reason, words) = (LockReasons.DisplayChanged, "the display changed — presenting free; SYNC REJOIN when ready");
            return true;
        }
        if (_hasCount && f.MonoNs - _lastAdvanceNs >= stallNs)
        {
            (reason, words) = (LockReasons.CountStalled, "the frame count stopped for 3 refreshes — presenting free; SYNC REJOIN when ready");
            return true;
        }
        if (!joining && _unsyncedSinceNs >= 0 && f.MonoNs - _unsyncedSinceNs >= stallNs)
        {
            (reason, words) = (LockReasons.BoardUnsynced, "the sync board lost its sync for 3 refreshes — presenting free; SYNC REJOIN when ready");
            return true;
        }
        (reason, words) = (LockReasons.None, "");
        return false;
    }

    /// <summary>What a join needs: a board, configured, no sync cable into a switch, and not armed unless blacked out.</summary>
    private static bool Precondition(in LockFacts f, out string reason, out string words)
    {
        if (!f.BoardPresent)
        {
            (reason, words) = (LockReasons.NoBoard, "no sync board on this node: the output presents free, measured");
            return false;
        }
        if (!f.BoardConfigured)
        {
            (reason, words) = (LockReasons.NotConfigured,
                "the sync board is not configured: once, as administrator, enable it as server on the timing node and as client on the others (or Frame Lock in the driver's control panel), then SYNC APPLY again");
            return false;
        }
        if (f.CableIntoSwitch)
        {
            (reason, words) = (LockReasons.CableIntoSwitch,
                "a sync cable runs into a network switch: the board's RJ45 ports go board to board, never to Ethernet — move the cable, then SYNC APPLY again");
            return false;
        }
        if (f.Armed && !f.BlackedOut)
        {
            (reason, words) = (LockReasons.Armed, "armed: joining the lock may step the picture — black out first, or SHOW DISARM");
            return false;
        }
        (reason, words) = (LockReasons.None, "");
        return true;
    }

    /// <summary>The wire word of a state.</summary>
    public static string Word(LockState state) => state switch
    {
        LockState.FreeMeasured => "free-measured",
        LockState.Grouped => "grouped",
        LockState.SoftLocked => "soft-locked",
        LockState.Framelocked => "framelocked",
        LockState.HouseLocked => "house-locked",
        LockState.FellOut => "fell-out",
        LockState.Joining => "joining",
        _ => "free-unknown",
    };
}
