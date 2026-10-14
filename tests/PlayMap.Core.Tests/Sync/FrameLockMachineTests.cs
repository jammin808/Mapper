using PlayMap.Core.Sync;

namespace PlayMap.Core.Tests.Sync;

/// <summary>The lock machine, scripted refresh by refresh: a board that syncs, a count that advances (or stops), the show's arm
/// and boundary, the group's intent. Every step's state and actions are asserted, so each rule is a transition seen.</summary>
public sealed class FrameLockMachineTests
{
    private const double Period = 1e9 / 60;

    /// <summary>A scripted output at 60 Hz: each <see cref="Tick"/> is one refresh (the count advances unless stalled).</summary>
    private sealed class Rig
    {
        public readonly FrameLockMachine Machine = new();
        public long Now = 10_000_000_000;
        public uint Count = 0xFFFFC0; // near the 24-bit wrap: the lock runs across it
        public bool Board = true, Configured = true, Synced = true, Cable, Armed, Blackout, Boundary = true, Bound = true;
        public bool Stall, TimedOut, Busy, DisplayChanged, Settled = true, CountValid = true, Rejoin, Auto = true, House, JoinRefused;
        public LockIntent Intent = LockIntent.None;
        public readonly List<(LockState State, LockActions Actions)> Seen = [];

        /// <summary>Every transition a step made: from, to, and the reason word it set (empty when the step set none).</summary>
        public readonly HashSet<(LockState From, LockState To, string Cause)> Edges = [];

        public LockActions Tick()
        {
            Now += (long)Math.Ceiling(Period);
            if (!Stall)
                Count = (Count + 1) & 0xFFFFFF;
            var facts = new LockFacts(Now, Period, Settled, Board, Configured, Synced, Synced, House, Cable, true, Bound, CountValid, Count,
                TimedOut, Busy, DisplayChanged, Armed, Blackout, Boundary, Intent)
            { RejoinAsked = Rejoin, AutoRejoin = Auto, JoinRefused = JoinRefused, JoinWords = JoinRefused ? "the swap chain is busy" : null };
            var (from, reasonBefore) = (Machine.State, Machine.Reason);
            var actions = Machine.Step(in facts);
            Rejoin = false;
            Seen.Add((Machine.State, actions));
            Edges.Add((from, Machine.State, from != Machine.State || Machine.Reason != reasonBefore ? Machine.Reason : ""));
            return actions;
        }

        public void Ticks(int n)
        {
            for (var i = 0; i < n; i++)
                Tick();
        }

        public void Apply(long seq = 1) => Intent = new LockIntent(true, 1, 1, seq);

        public void Lock()
        {
            Apply();
            Assert.Equal(LockActions.Join | LockActions.Bind | LockActions.OverridePresent, Tick());
            Ticks(FrameLockMachine.ConfirmRefreshes);
            Assert.Equal(LockState.Framelocked, Machine.State);
        }
    }

    private const LockActions LeaveAll = LockActions.Unbind | LockActions.Leave | LockActions.RestorePresent;

    [Fact]
    public void AFreeOutputWithNothingAskedStaysFreeMeasuredOrUnknown()
    {
        var rig = new Rig();
        Assert.Equal(LockActions.None, rig.Tick());
        Assert.Equal(LockState.FreeMeasured, rig.Machine.State);
        rig.Settled = false;
        rig.Tick();
        Assert.Equal(LockState.FreeUnknown, rig.Machine.State);
        Assert.Equal("free-unknown", FrameLockMachine.Word(rig.Machine.State));
        Assert.Equal("", rig.Machine.Words);
    }

    [Fact]
    public void AJoinLocksAfter120GoodRefreshesAcrossTheWrap()
    {
        var rig = new Rig();
        rig.Apply();
        Assert.Equal(LockActions.Join | LockActions.Bind | LockActions.OverridePresent, rig.Tick());
        Assert.Equal(LockState.Joining, rig.Machine.State);
        rig.Ticks(FrameLockMachine.ConfirmRefreshes - 1);
        Assert.Equal(LockState.Joining, rig.Machine.State);
        Assert.Equal(LockActions.None, rig.Tick());
        Assert.Equal(LockState.Framelocked, rig.Machine.State);
        Assert.Equal("framelocked", FrameLockMachine.Word(rig.Machine.State));
        Assert.True(rig.Machine.IsLocked);
        Assert.True(rig.Count < 0x100, "the count wrapped during the join");
        Assert.Equal(rig.Now, rig.Machine.LockedSinceNs);
        Assert.Contains("swap group 1, barrier 1", rig.Machine.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountThatJumpsStartsTheRunOfGoodRefreshesAgain()
    {
        var rig = new Rig();
        rig.Apply();
        rig.Tick();
        rig.Ticks(100);
        rig.Count += 5;
        rig.Tick();
        Assert.Equal(0, rig.Machine.GoodRefreshes);
        rig.Ticks(FrameLockMachine.ConfirmRefreshes - 1);
        Assert.Equal(LockState.Joining, rig.Machine.State);
        rig.Tick();
        Assert.Equal(LockState.Framelocked, rig.Machine.State);
    }

    [Fact]
    public void AnUnboundBarrierOrAnUnsyncedBoardNeverConfirmsAndTheJoinTimesOutAfterFiveSeconds()
    {
        var rig = new Rig { Bound = false };
        rig.Apply();
        rig.Tick();
        rig.Ticks(FrameLockMachine.ConfirmRefreshes * 2);
        Assert.Equal(LockState.Joining, rig.Machine.State);
        rig.Ticks(5 * 60 - FrameLockMachine.ConfirmRefreshes * 2 - 1);
        var actions = rig.Tick();
        Assert.Equal(LockState.FellOut, rig.Machine.State);
        Assert.Equal(LeaveAll | LockActions.Alarm, actions);
        Assert.Equal(LockReasons.JoinTimeout, rig.Machine.Reason);
        Assert.Contains("did not lock within 5 s", rig.Machine.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void HouseSyncLocksAsHouseLocked()
    {
        var rig = new Rig();
        rig.Apply();
        rig.Tick();
        var machine = rig.Machine;
        // House sync is a fact of the board; a dedicated rig step with it on.
        for (var i = 0; i < FrameLockMachine.ConfirmRefreshes; i++)
        {
            rig.Now += (long)Math.Ceiling(Period);
            rig.Count = (rig.Count + 1) & 0xFFFFFF;
            machine.Step(new LockFacts(rig.Now, Period, true, true, true, true, true, true, false, true, true, true, rig.Count,
                false, false, false, false, false, true, rig.Intent));
        }
        Assert.Equal(LockState.HouseLocked, machine.State);
        Assert.StartsWith("house-locked", machine.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void AStallAtThreePeriodsLeavesTheBarrierAndTheOutputKeepsPresenting()
    {
        var rig = new Rig();
        rig.Lock();
        rig.Stall = true;
        Assert.Equal(LockActions.None, rig.Tick());
        Assert.Equal(LockActions.None, rig.Tick());
        Assert.Equal(LockState.Framelocked, rig.Machine.State);
        var actions = rig.Tick();
        Assert.Equal(LeaveAll | LockActions.Alarm, actions);
        Assert.Equal(LockState.FellOut, rig.Machine.State);
        Assert.Equal(LockReasons.CountStalled, rig.Machine.Reason);
        Assert.Equal(1, rig.Machine.FellOutCount);
        // Fell out: no further action, ever a wait — the output presents free on its own vblank.
        rig.Stall = false;
        rig.Armed = true;
        rig.Ticks(30);
        Assert.All(rig.Seen.TakeLast(30), s => Assert.Equal(LockActions.None, s.Actions));
        Assert.Contains("presenting free", rig.Machine.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void APresentWaitThatTimedOutFallsOutAtOnce()
    {
        var rig = new Rig();
        rig.Lock();
        rig.TimedOut = true;
        Assert.Equal(LeaveAll | LockActions.Alarm, rig.Tick());
        Assert.Equal(LockReasons.Stall, rig.Machine.Reason);
        Assert.Contains("the barrier stalled 3 frames", rig.Machine.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoardSyncLossIsDebouncedOverThreePeriods()
    {
        var rig = new Rig();
        rig.Lock();
        rig.Synced = false;
        rig.Ticks(2);
        rig.Synced = true;
        rig.Tick();
        Assert.Equal(LockState.Framelocked, rig.Machine.State);

        rig.Synced = false;
        rig.Tick();
        rig.Tick();
        rig.Tick();
        Assert.Equal(LockState.Framelocked, rig.Machine.State);
        Assert.Equal(LeaveAll | LockActions.Alarm, rig.Tick());
        Assert.Equal(LockReasons.BoardUnsynced, rig.Machine.Reason);
    }

    [Fact]
    public void ABusyDeviceOrAChangedDisplayFallsOut()
    {
        var busy = new Rig();
        busy.Lock();
        busy.Busy = true;
        Assert.Equal(LeaveAll | LockActions.Alarm, busy.Tick());
        Assert.Equal(LockReasons.DeviceBusy, busy.Machine.Reason);

        var display = new Rig();
        display.Lock();
        display.DisplayChanged = true;
        Assert.Equal(LeaveAll | LockActions.Alarm, display.Tick());
        Assert.Equal(LockReasons.DisplayChanged, display.Machine.Reason);
    }

    [Fact]
    public void NoRejoinWhileArmedNotEvenAtABoundaryAfterLongSync()
    {
        var rig = new Rig();
        rig.Lock();
        rig.TimedOut = true;
        rig.Tick();
        rig.TimedOut = false;
        rig.Armed = true;
        rig.Ticks(20 * 60);
        Assert.Equal(LockState.FellOut, rig.Machine.State);
        Assert.Equal(0, rig.Machine.Rejoins);
    }

    [Fact]
    public void ARejoinAskedWhileArmedIsRefusedUnlessBlackedOut()
    {
        var rig = new Rig();
        rig.Lock();
        rig.TimedOut = true;
        rig.Tick();
        rig.TimedOut = false;
        rig.Armed = true;
        rig.Rejoin = true;
        Assert.Equal(LockActions.None, rig.Tick());
        Assert.Equal(LockState.FellOut, rig.Machine.State);
        Assert.Equal(LockReasons.Armed, rig.Machine.Reason);

        rig.Blackout = true;
        rig.Rejoin = true;
        Assert.Equal(LockActions.Join | LockActions.Bind | LockActions.OverridePresent, rig.Tick());
        Assert.Equal(LockState.Joining, rig.Machine.State);
        Assert.Equal(1, rig.Machine.Rejoins);
        rig.Ticks(FrameLockMachine.ConfirmRefreshes);
        Assert.Equal(LockState.Framelocked, rig.Machine.State);
    }

    [Fact]
    public void DisarmedItRejoinsByItselfAfterTenSecondsOfSyncAtAnIdleBoundary()
    {
        var rig = new Rig();
        rig.Lock();
        rig.TimedOut = true;
        rig.Tick();
        rig.TimedOut = false;
        rig.Boundary = false;
        rig.Ticks(15 * 60);
        Assert.Equal(LockState.FellOut, rig.Machine.State);

        // At a boundary, the board synced for 10 s already: it joins again on the next refresh.
        rig.Boundary = true;
        Assert.Equal(LockActions.Join | LockActions.Bind | LockActions.OverridePresent, rig.Tick());
        Assert.Equal(1, rig.Machine.Rejoins);
    }

    [Fact]
    public void AutoRejoinWaitsForTenSecondsOfSyncFromTheLastLoss()
    {
        var rig = new Rig();
        rig.Lock();
        rig.Synced = false;
        rig.Ticks(4);
        Assert.Equal(LockState.FellOut, rig.Machine.State);
        rig.Synced = true;
        rig.Ticks(10 * 60 - 1);
        Assert.Equal(LockState.FellOut, rig.Machine.State);
        rig.Ticks(2);
        Assert.Equal(LockState.Joining, rig.Machine.State);
    }

    [Fact]
    public void WithAutoRejoinOffOnlyARejoinAskJoinsAgain()
    {
        var rig = new Rig { Auto = false };
        rig.Lock();
        rig.Busy = true;
        rig.Tick();
        rig.Busy = false;
        rig.Ticks(20 * 60);
        Assert.Equal(LockState.FellOut, rig.Machine.State);
        rig.Rejoin = true;
        rig.Tick();
        Assert.Equal(LockState.Joining, rig.Machine.State);
    }

    [Fact]
    public void ANewApplyWhileFellOutAsksAgain()
    {
        var rig = new Rig();
        rig.Lock();
        rig.Busy = true;
        rig.Tick();
        rig.Busy = false;
        rig.Boundary = false;
        rig.Tick();
        Assert.Equal(LockState.FellOut, rig.Machine.State);
        rig.Apply(seq: 2);
        Assert.Equal(LockActions.Join | LockActions.Bind | LockActions.OverridePresent, rig.Tick());
    }

    [Fact]
    public void ACableIntoASwitchIsRefusedWithWordsAndNotRetriedEveryFrame()
    {
        var rig = new Rig { Cable = true };
        rig.Apply();
        Assert.Equal(LockActions.None, rig.Tick());
        Assert.Equal(LockState.FreeMeasured, rig.Machine.State);
        Assert.Equal(LockReasons.CableIntoSwitch, rig.Machine.Reason);
        Assert.Contains("network switch", rig.Machine.Words, StringComparison.Ordinal);
        rig.Cable = false;
        rig.Ticks(10);
        Assert.Equal(LockState.FreeMeasured, rig.Machine.State);
        // A new wish (the operator moved the cable and applied again) joins.
        rig.Apply(seq: 2);
        Assert.Equal(LockActions.Join | LockActions.Bind | LockActions.OverridePresent, rig.Tick());
    }

    [Fact]
    public void NoBoardANotConfiguredBoardAndArmedAreRefusedByTheirReasons()
    {
        var none = new Rig { Board = false };
        none.Apply();
        none.Tick();
        Assert.Equal(LockReasons.NoBoard, none.Machine.Reason);

        var unconfigured = new Rig { Configured = false };
        unconfigured.Apply();
        unconfigured.Tick();
        Assert.Equal(LockReasons.NotConfigured, unconfigured.Machine.Reason);
        Assert.Contains("administrator", unconfigured.Machine.Words, StringComparison.Ordinal);

        var armed = new Rig { Armed = true };
        armed.Apply();
        armed.Tick();
        Assert.Equal(LockReasons.Armed, armed.Machine.Reason);
        Assert.Equal(LockState.FreeMeasured, armed.Machine.State);

        var dark = new Rig { Armed = true, Blackout = true };
        dark.Apply();
        Assert.Equal(LockActions.Join | LockActions.Bind | LockActions.OverridePresent, dark.Tick());
    }

    [Fact]
    public void ReleasingTheIntentLeavesFromJoiningAndFromLocked()
    {
        var joining = new Rig();
        joining.Apply();
        joining.Tick();
        joining.Intent = LockIntent.None;
        Assert.Equal(LeaveAll, joining.Tick());
        Assert.Equal(LockState.FreeMeasured, joining.Machine.State);

        var locked = new Rig();
        locked.Lock();
        locked.Intent = LockIntent.None;
        Assert.Equal(LeaveAll, locked.Tick());
        Assert.Equal(LockState.FreeMeasured, locked.Machine.State);
        Assert.Equal(-1, locked.Machine.LockedSinceNs);
        Assert.Equal(LockActions.None, locked.Tick());

        var fell = new Rig();
        fell.Lock();
        fell.Busy = true;
        fell.Tick();
        fell.Intent = LockIntent.None;
        Assert.Equal(LockActions.None, fell.Tick());
        Assert.Equal(LockState.FreeMeasured, fell.Machine.State);
        Assert.Equal("", fell.Machine.Reason);
    }

    [Fact]
    public void ACountThatStopsWhileJoiningFallsOutAfterThreePeriods()
    {
        var rig = new Rig();
        rig.Apply();
        rig.Tick();
        rig.Ticks(10);
        rig.Stall = true;
        rig.Ticks(2);
        Assert.Equal(LockState.Joining, rig.Machine.State);
        rig.Tick();
        Assert.Equal(LockState.FellOut, rig.Machine.State);
        Assert.Equal(LockReasons.CountStalled, rig.Machine.Reason);
    }

    [Fact]
    public void ADriverThatRefusedTheJoinFallsOutWithItsWords()
    {
        var rig = new Rig();
        rig.Apply();
        rig.Tick();
        rig.Now += (long)Math.Ceiling(Period);
        var actions = rig.Machine.Step(new LockFacts(rig.Now, Period, true, true, true, true, true, false, false, false, false, false, 0,
            false, false, false, false, false, true, rig.Intent) { JoinRefused = true, JoinWords = "the swap chain is busy" });
        Assert.Equal(LeaveAll | LockActions.Alarm, actions);
        Assert.Equal(LockReasons.JoinRefused, rig.Machine.Reason);
        Assert.Contains("the swap chain is busy", rig.Machine.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoRejoinWaitsTenSecondsAfterTheFallEvenWithTheBoardSyncedAllAlong()
    {
        var rig = new Rig();
        rig.Ticks(20 * 60);
        rig.Lock();
        rig.TimedOut = true;
        rig.Tick();
        rig.TimedOut = false;
        Assert.Equal(LockState.FellOut, rig.Machine.State);
        rig.Ticks(10 * 60 - 2);
        Assert.Equal(LockState.FellOut, rig.Machine.State);
        rig.Ticks(2);
        Assert.Equal(LockState.Joining, rig.Machine.State);
        Assert.Equal(1, rig.Machine.Rejoins);
    }

    [Fact]
    public void AJoinOnACountThatStoodStillBeforeItIsJudgedFromTheJoin()
    {
        var rig = new Rig { Stall = true };
        rig.Ticks(30);
        rig.Apply();
        Assert.Equal(LockActions.Join | LockActions.Bind | LockActions.OverridePresent, rig.Tick());
        // Two periods since the join: the count has not yet stood still for three of the join's own refreshes.
        rig.Ticks(2);
        Assert.Equal(LockState.Joining, rig.Machine.State);
        Assert.Equal(LeaveAll | LockActions.Alarm, rig.Tick());
        Assert.Equal(LockReasons.CountStalled, rig.Machine.Reason);
    }

    [Fact]
    public void TheScriptsCoverAtLeastTwentyFiveDistinctTransitions()
    {
        // Each (from, to, reason) a script drives, gathered over short scripts that each start from a fresh output.
        var edges = new HashSet<(LockState From, LockState To, string Cause)>();
        void Run(Action<Rig> script)
        {
            var rig = new Rig();
            script(rig);
            edges.UnionWith(rig.Edges);
        }
        void Fall(Rig r, Action<Rig> fault, int ticks)
        {
            fault(r);
            r.Ticks(ticks);
            Assert.Equal(LockState.FellOut, r.Machine.State);
        }
        void HouseLock(Rig r)
        {
            r.House = true;
            r.Apply();
            r.Ticks(1 + FrameLockMachine.ConfirmRefreshes);
            Assert.Equal(LockState.HouseLocked, r.Machine.State);
            r.Tick();
        }

        // Free, and every refusal by its reason.
        Run(r => { r.Ticks(2); r.Settled = false; r.Tick(); r.Settled = true; r.Tick(); });
        Run(r => { r.Tick(); r.Cable = true; r.Apply(); r.Tick(); });
        Run(r => { r.Tick(); r.Board = false; r.Apply(); r.Tick(); });
        Run(r => { r.Tick(); r.Configured = false; r.Apply(); r.Tick(); });
        Run(r => { r.Tick(); r.Armed = true; r.Apply(); r.Tick(); });
        // Joining: locks, house-locks; times out, refused by the driver, the count stalls, the wait times out; released.
        Run(r => { r.Lock(); r.Tick(); });
        Run(r => { r.Tick(); r.Lock(); });
        Run(HouseLock);
        Run(r => { r.Bound = false; r.Apply(); r.Ticks(5 * 60 + 2); });
        Run(r => { r.Apply(); r.Tick(); r.JoinRefused = true; r.Tick(); });
        Run(r => { r.Apply(); r.Ticks(5); r.Stall = true; r.Ticks(3); });
        Run(r => { r.Apply(); r.Tick(); r.TimedOut = true; r.Tick(); });
        Run(r => { r.Tick(); r.Apply(); r.Tick(); r.Intent = LockIntent.None; r.Tick(); });
        Run(r => { r.Settled = false; r.Apply(); r.Tick(); r.Intent = LockIntent.None; r.Tick(); });
        // Locked: each way of falling out, and released.
        Run(r => { r.Lock(); Fall(r, x => x.TimedOut = true, 1); });
        Run(r => { r.Lock(); Fall(r, x => x.Stall = true, 3); });
        Run(r => { r.Lock(); Fall(r, x => x.Synced = false, 4); });
        Run(r => { r.Lock(); Fall(r, x => x.Busy = true, 1); });
        Run(r => { r.Lock(); Fall(r, x => x.DisplayChanged = true, 1); });
        Run(r => { r.Lock(); r.Intent = LockIntent.None; r.Tick(); });
        Run(r => { r.Lock(); r.Settled = false; r.Intent = LockIntent.None; r.Tick(); });
        Run(r => { HouseLock(r); Fall(r, x => x.TimedOut = true, 1); });
        Run(r => { HouseLock(r); r.Intent = LockIntent.None; r.Tick(); });
        // Fell out: stays (armed), a rejoin refused while armed, a rejoin, released settled and unsettled.
        Run(r => { r.Lock(); Fall(r, x => x.Busy = true, 1); r.Busy = false; r.Armed = true; r.Tick(); r.Rejoin = true; r.Tick(); });
        Run(r => { r.Lock(); Fall(r, x => x.Busy = true, 1); r.Busy = false; r.Rejoin = true; r.Tick(); });
        Run(r => { r.Lock(); Fall(r, x => x.Busy = true, 1); r.Intent = LockIntent.None; r.Tick(); });
        Run(r => { r.Lock(); Fall(r, x => x.Busy = true, 1); r.Settled = false; r.Intent = LockIntent.None; r.Tick(); });

        const LockState Fu = LockState.FreeUnknown, Fm = LockState.FreeMeasured, Jn = LockState.Joining, Fl = LockState.Framelocked,
            Hl = LockState.HouseLocked, Fo = LockState.FellOut;
        (LockState, LockState, string)[] expected =
        [
            (Fu, Fm, ""), (Fm, Fu, ""), (Fm, Fm, ""), (Fu, Jn, ""), (Fm, Jn, ""), (Jn, Jn, ""), (Jn, Fl, ""), (Jn, Hl, ""), (Fl, Fl, ""),
            (Hl, Hl, ""),
            (Fm, Fm, LockReasons.CableIntoSwitch), (Fm, Fm, LockReasons.NoBoard), (Fm, Fm, LockReasons.NotConfigured), (Fm, Fm, LockReasons.Armed),
            (Jn, Fo, LockReasons.JoinTimeout), (Jn, Fo, LockReasons.JoinRefused), (Jn, Fo, LockReasons.CountStalled), (Jn, Fo, LockReasons.Stall),
            (Jn, Fm, ""), (Jn, Fu, ""),
            (Fl, Fo, LockReasons.Stall), (Fl, Fo, LockReasons.CountStalled), (Fl, Fo, LockReasons.BoardUnsynced),
            (Fl, Fo, LockReasons.DeviceBusy), (Fl, Fo, LockReasons.DisplayChanged), (Fl, Fm, ""), (Fl, Fu, ""),
            (Hl, Fo, LockReasons.Stall), (Hl, Fm, ""),
            (Fo, Fo, ""), (Fo, Fo, LockReasons.Armed), (Fo, Jn, ""), (Fo, Fm, ""), (Fo, Fu, ""),
        ];
        foreach (var edge in expected)
            Assert.Contains(edge, edges);
        Assert.True(edges.Count >= 25, $"{edges.Count} distinct transitions");
    }
}
