using System.Collections.Frozen;
using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Sync;
using PlayMap.Render.Engine;
using PlayMap.Render.Platform;
using PlayMap.Render.Sync;
using PlayMap.Render.Timing;

namespace PlayMap.Engine.Presentation;

/// <summary>The frame lock on the render thread: one <see cref="FrameLockMachine"/> per enabled output, stepped once a frame with the
/// facts of that frame (the board as last polled, the output's clock and present wait, its frame count, the show's arm and
/// boundary, the group's wish), the driver's actions performed at once, and the views republished only when one changed.
///
/// <list type="bullet">
/// <item><b>Never stalls.</b> The driver's barrier wait timing out (3 refreshes) or the board's count standing still for 3 periods
/// is the watchdog's evidence: the machine leaves the barrier and the output presents free from its next present. How long a
/// present call took is never evidence: it holds the node's own work, not the barrier.</item>
/// <item><b>Content time under the lock.</b> While the pacing output is locked with a valid count, the next frame's show time is
/// the anchor's mapping of the next count (<see cref="LockCounter"/>), the same on every node; the anchor is this node's own
/// (re-made every 60 s from the pacer's fitted vblank) unless a leader's was offered.</item>
/// <item><b>The board</b> is read off the render thread once a second (a driver's board calls may take milliseconds).</item>
/// </list>
/// Verbs set the wish from any thread; the render thread reads it once a frame. Allocation-free per frame once warm.</summary>
internal sealed class FrameLockHost : IDisposable
{
    private sealed class Slot
    {
        public readonly FrameLockMachine Machine = new();
        public bool Joined;
        public bool JoinRefused;
        public string? JoinWords;
        public IOutputPresenter? Presenter;
        public bool PresenterKnown;
        public LockView? View;
        public bool CountValid;
        public uint Count;
        public long SeenFrame;
    }

    private readonly IFrameLockDriver _driver;
    private readonly bool _auto;
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly List<string> _gone = [];
    private readonly LockCounter _counter = new();
    private readonly Timer _boardPoll;
    private readonly Lock _gate = new();
    private readonly List<(IReadOnlyCollection<string> Ids, Func<LockView, bool> Settled, long MinFrame, TaskCompletionSource<IReadOnlyDictionary<string, LockView>> Done)> _waiters = [];
    private volatile FrameLockBoard? _board;
    private LockIntent _intent = LockIntent.None;
    private long _seq;
    private ImmutableHashSet<string> _heldOut = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private ImmutableHashSet<string> _rejoin = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private volatile IReadOnlyDictionary<string, LockView> _views = FrozenDictionary<string, LockView>.Empty;
    private volatile string _timeSource = "clock";
    private bool _anchorOffered;
    private long _anchorSeq;
    private long _frame;
    private int _waiting;
    private bool _disposed;

    public FrameLockHost(IFrameLockDriver driver, bool auto)
    {
        ArgumentNullException.ThrowIfNull(driver);
        _driver = driver;
        _auto = auto;
        _boardPoll = new Timer(static s => ((FrameLockHost)s!).PollBoard(), this, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    /// <summary>A view changed (render thread): STATE moves.</summary>
    public event Action? Changed;

    /// <summary>An output fell out of the lock (render thread): its id and the words.</summary>
    public event Action<string, string>? FellOut;

    public IFrameLockDriver Driver => _driver;

    public FrameLockBoard? Board => _board;

    public IReadOnlyDictionary<string, LockView> Views => _views;

    public string TimeSource => _timeSource;

    public LockIntent Intent
    {
        get
        {
            lock (_gate)
                return _intent;
        }
    }

    public IReadOnlyCollection<string> HeldOut
    {
        get
        {
            lock (_gate)
                return _heldOut;
        }
    }

    public void Apply(uint swapGroup, uint barrier)
    {
        lock (_gate)
        {
            _intent = new LockIntent(true, swapGroup, barrier, ++_seq);
            _heldOut = _heldOut.Clear();
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _intent = new LockIntent(false, _intent.SwapGroup, _intent.Barrier, ++_seq);
            _heldOut = _heldOut.Clear();
        }
    }

    public void Leave(IReadOnlyCollection<string> outputIds)
    {
        lock (_gate)
            _heldOut = _heldOut.Union(outputIds);
    }

    public void Rejoin(IReadOnlyCollection<string> outputIds)
    {
        lock (_gate)
        {
            _heldOut = _heldOut.Except(outputIds);
            _rejoin = _rejoin.Union(outputIds);
        }
    }

    public LockAnchor? LocalAnchor()
    {
        lock (_gate)
            return _counter.Active;
    }

    public void OfferAnchor(LockAnchor anchor)
    {
        lock (_gate)
        {
            _anchorOffered = true;
            _counter.Offer(anchor);
        }
    }

    public Task<IReadOnlyDictionary<string, LockView>> ObserveAsync(IReadOnlyCollection<string> outputIds, Func<LockView, bool> settled,
        TimeSpan within, CancellationToken ct)
    {
        // Answered from a frame stepped after the ask: the wish set just before is seen by then, so a view from before it never
        // answers for it.
        var done = new TaskCompletionSource<IReadOnlyDictionary<string, LockView>>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters.Add((outputIds, settled, _frame + 1, done));
            _waiting = _waiters.Count;
        }
        return WaitAsync(done, within, ct);
    }

    private async Task<IReadOnlyDictionary<string, LockView>> WaitAsync(TaskCompletionSource<IReadOnlyDictionary<string, LockView>> done,
        TimeSpan within, CancellationToken ct)
    {
        try
        {
            return await done.Task.WaitAsync(within, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Time is up: the locks as they are now are the answer (the caller reports what it observed).
            return _views;
        }
        finally
        {
            lock (_gate)
            {
                _waiters.RemoveAll(w => w.Done == done);
                _waiting = _waiters.Count;
            }
        }
    }

    private static bool Satisfied(IReadOnlyDictionary<string, LockView> views, IReadOnlyCollection<string> ids, Func<LockView, bool> settled)
    {
        foreach (var id in ids)
            if (!views.TryGetValue(id, out var v) || !settled(v))
                return false;
        return true;
    }

    /// <summary>One frame's step for every enabled output (render thread, after the frame's presents).</summary>
    public void Step(RenderEngine renderer, Project project, string? pacerId, long monoNow, long leaderNowNs, bool armed, bool blackedOut,
        bool atBoundary)
    {
        LockIntent intent;
        ImmutableHashSet<string> heldOut, rejoin;
        lock (_gate)
        {
            intent = _intent;
            heldOut = _heldOut;
            rejoin = _rejoin;
            if (!rejoin.IsEmpty)
                _rejoin = _rejoin.Clear();
            _frame++;
        }
        var board = _board;
        var changed = false;
        foreach (var output in project.Outputs)
        {
            if (!output.Enabled || output.Width < 1 || output.Height < 1)
                continue;
            if (!_slots.TryGetValue(output.Id, out var slot))
            {
                slot = new Slot();
                _slots[output.Id] = slot;
            }
            slot.SeenFrame = _frame;
            changed |= StepOutput(renderer, output.Id, slot, board, intent, heldOut.Contains(output.Id), rejoin.Contains(output.Id), monoNow,
                armed, blackedOut, atBoundary);
            if (output.Id == pacerId)
                Count(slot, renderer.Present(output.Id), monoNow, leaderNowNs);
        }
        if (pacerId is null || !_slots.TryGetValue(pacerId, out var pacer) || !pacer.Machine.IsLocked)
            _timeSource = "clock";
        foreach (var (id, slot) in _slots)
            if (slot.SeenFrame != _frame)
                _gone.Add(id);
        foreach (var id in _gone)
        {
            var slot = _slots[id];
            if (slot.Joined)
                _driver.Leave(id, slot.Presenter);
            _slots.Remove(id);
            changed = true;
        }
        _gone.Clear();
        if (changed)
            Publish();
        else if (Volatile.Read(ref _waiting) > 0)
            AnswerWaiters(_views);
    }

    private bool StepOutput(RenderEngine renderer, string id, Slot slot, FrameLockBoard? board, LockIntent intent, bool heldOut, bool rejoin,
        long monoNow, bool armed, bool blackedOut, bool atBoundary)
    {
        var presenter = renderer.PresenterOf(id);
        var displayChanged = slot.PresenterKnown && !ReferenceEquals(presenter, slot.Presenter) && slot.Joined;
        slot.Presenter = presenter;
        slot.PresenterKnown = true;
        var present = renderer.Present(id);
        var hz = present is { RefreshHz: > 1 } p ? p.RefreshHz : 60;
        var period = 1e9 / hz;
        var outputIntent = heldOut ? intent with { Apply = false } : intent;
        uint count = 0;
        var valid = slot.Joined && _driver.TryFrameCount(id, presenter, out count);
        var timedOut = slot.Joined && _driver.WaitTimedOut(id);
        var facts = new LockFacts(monoNow, period, present?.Settled ?? false, board is not null, board?.Configured ?? false,
            board?.Synced ?? false, board?.SignalAvailable ?? false, board?.HouseSync ?? false, board?.CableIntoSwitch ?? false, slot.Joined,
            slot.Joined && _driver.BarrierBound(id), valid, count, timedOut, false, displayChanged, armed, blackedOut, atBoundary, outputIntent)
        {
            RejoinAsked = rejoin,
            AutoRejoin = _auto,
            JoinRefused = slot.JoinRefused,
            JoinWords = slot.JoinWords,
        };
        slot.JoinRefused = false;
        var actions = slot.Machine.Step(in facts);
        if ((actions & (LockActions.Unbind | LockActions.Leave)) != 0 && slot.Joined)
        {
            _driver.Leave(id, presenter);
            slot.Joined = false;
        }
        if ((actions & LockActions.Join) != 0)
        {
            slot.Joined = _driver.Join(id, presenter, outputIntent.SwapGroup, outputIntent.Barrier, out var words);
            slot.JoinRefused = !slot.Joined;
            slot.JoinWords = slot.Joined ? null : words;
        }
        if ((actions & LockActions.Alarm) != 0)
            FellOut?.Invoke(id, slot.Machine.Words);
        slot.CountValid = valid;
        slot.Count = count;
        var m = slot.Machine;
        if (slot.View is { } v && v.State == m.State && v.Reason == m.Reason && v.Words == m.Words && v.FellOut == m.FellOutCount
            && v.Rejoins == m.Rejoins)
            return false;
        slot.View = new LockView(id, m.State, m.Reason, m.Words, m.FellOutCount, m.Rejoins);
        return true;
    }

    /// <summary>The pacer's count drives the counter; a node that leads its own lock re-anchors every 60 s from the pacer's fitted
    /// vblank (its show time: the leader's now less the time since that vblank).</summary>
    private void Count(Slot pacer, PresentSnapshot? present, long monoNow, long leaderNowNs)
    {
        if (!pacer.Machine.IsLocked || !pacer.CountValid)
            return;
        lock (_gate)
        {
            _counter.Read(pacer.Count);
            if (!_anchorOffered && present is { } p && p.LastVblankNs > 0 && _counter.DueForReanchor(pacer.Count, p.RefreshHz))
            {
                var vblankLeaderNs = leaderNowNs - (monoNow - p.LastVblankNs);
                _counter.Offer(LockCounter.Make(pacer.Count, vblankLeaderNs, (long)Math.Round(1e12 / p.RefreshHz), ++_anchorSeq,
                    wide: _counter.Wide));
            }
        }
    }

    /// <summary>The show time of the next vblank under the lock (the pacer's next count); false while the pacer is not locked or no
    /// anchor maps its count (the caller uses the clock).</summary>
    public bool TryNextLeaderNs(string? pacerId, out long leaderNs)
    {
        leaderNs = 0;
        if (pacerId is null || !_slots.TryGetValue(pacerId, out var pacer) || !pacer.Machine.IsLocked || !pacer.CountValid)
            return false;
        bool ok;
        lock (_gate)
            ok = _counter.TryLeaderNsAt(_counter.Next(pacer.Count), out leaderNs);
        _timeSource = ok ? "count" : "clock";
        return ok;
    }

    private void Publish()
    {
        var views = new Dictionary<string, LockView>(_slots.Count, StringComparer.Ordinal);
        foreach (var (id, slot) in _slots)
            if (slot.View is { } v)
                views[id] = v;
        var frozen = views.ToFrozenDictionary(StringComparer.Ordinal);
        lock (_gate)
            _views = frozen;
        AnswerWaiters(frozen);
        Changed?.Invoke();
    }

    /// <summary>Completes every waiter whose outputs satisfy it in these views, from its first frame on (render thread).</summary>
    private void AnswerWaiters(IReadOnlyDictionary<string, LockView> views)
    {
        List<TaskCompletionSource<IReadOnlyDictionary<string, LockView>>>? due = null;
        lock (_gate)
        {
            foreach (var w in _waiters)
                if (_frame >= w.MinFrame && Satisfied(views, w.Ids, w.Settled))
                    (due ??= []).Add(w.Done);
        }
        if (due is not null)
            foreach (var d in due)
                d.TrySetResult(views);
    }

    private void PollBoard()
    {
        if (_disposed)
            return;
        try
        {
            _board = _driver.Board();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.ExternalException)
        {
            // A driver that failed a read has no board to show this second; the next poll asks again.
            _board = null;
        }
    }

    /// <summary>Leaves every joined output (render thread) and stops polling.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _boardPoll.Dispose();
        foreach (var (id, slot) in _slots)
            if (slot.Joined)
                _driver.Leave(id, slot.Presenter);
        _slots.Clear();
        _driver.Dispose();
        lock (_gate)
        {
            foreach (var w in _waiters)
                w.Done.TrySetResult(_views);
            _waiters.Clear();
        }
    }
}
