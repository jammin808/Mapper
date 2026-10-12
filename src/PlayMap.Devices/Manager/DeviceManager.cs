using System.Collections.Immutable;
using System.Net;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Drivers.PjLink;
using PlayMap.Devices.Model;

namespace PlayMap.Devices.Manager;

/// <summary>How the manager paces itself. Liveness follows the Show Bus's shape (stale after a few missed beats,
/// gone after more) scaled to each link's poll interval, never quicker than the Show Bus's own thresholds.</summary>
public sealed record DeviceManagerOptions
{
    /// <summary>How often the loop looks for due polls and re-judges liveness.</summary>
    public int TickMs { get; init; } = 250;

    /// <summary>Silent this many poll intervals = stale (amber).</summary>
    public double StalePolls { get; init; } = 2.5;

    /// <summary>Silent this many poll intervals = gone (red).</summary>
    public double GonePolls { get; init; } = 5;

    /// <summary>The longest wait between polls of a projector that keeps failing.</summary>
    public int MaxBackoffMs { get; init; } = 60_000;

    /// <summary>Lens positions are read on the first answer and then every this many polls (they change only
    /// when someone drives the lens).</summary>
    public int LensEveryPolls { get; init; } = 6;
}

public sealed class DeviceStateChangedEventArgs(DeviceState previous, DeviceState current) : EventArgs
{
    public DeviceState Previous { get; } = previous;

    public DeviceState Current { get; } = current;
}

/// <summary>
/// Every projector link, polled: a staggered schedule (default every 10 s), never more than one command in flight
/// per projector (polls and the engine's commands share one gate), back-off while a projector does not answer,
/// liveness judged from silence (live → stale → gone, as Show Bus peers are), and an event whenever a projector's
/// facts change. PJLink class 2 notifications fold in as they arrive. Thread-safe; the engine owns one.
/// </summary>
public sealed class DeviceManager : IAsyncDisposable
{
    private readonly IProjectorDriverFactory _drivers;
    private readonly IMonoClock _clock;
    private readonly IDeviceLog _log;
    private readonly DeviceManagerOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private PjLinkUdp? _notices;

    public DeviceManager(IProjectorDriverFactory drivers, IMonoClock clock, IDeviceLog log, DeviceManagerOptions? options = null)
    {
        _drivers = drivers;
        _clock = clock;
        _log = log;
        _options = options ?? new DeviceManagerOptions();
    }

    /// <summary>Raised (on the polling thread) when a projector's facts change; never for a poll that changed nothing.</summary>
    public event EventHandler<DeviceStateChangedEventArgs>? StateChanged;

    /// <summary>Every link's state, in link order.</summary>
    public ImmutableArray<DeviceState> States
    {
        get
        {
            lock (_gate)
                return _entries.Values.OrderBy(e => e.Order).Select(e => e.State).ToImmutableArray();
        }
    }

    public DeviceState? State(string linkId)
    {
        lock (_gate)
            return _entries.TryGetValue(linkId, out var e) ? e.State : null;
    }

    /// <summary>The driver for a link (its capabilities and caveats for the desk); commands go through <see cref="RunAsync"/>.</summary>
    public IProjectorDriver? Driver(string linkId)
    {
        lock (_gate)
            return _entries.TryGetValue(linkId, out var e) ? e.Driver : null;
    }

    /// <summary>Makes the managed links these: new ones are added (staggered across a poll interval), changed ones get a
    /// new driver and start over, missing ones are dropped.</summary>
    public async Task SetLinksAsync(IReadOnlyList<ProjectorLink> links)
    {
        var dropped = new List<Entry>();
        lock (_gate)
        {
            var now = _clock.NowNs;
            var keep = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < links.Count; i++)
            {
                var link = links[i];
                keep.Add(link.Id);
                if (_entries.TryGetValue(link.Id, out var existing))
                {
                    existing.Order = i;
                    if (existing.Link == link)
                        continue;
                    dropped.Add(existing);
                }
                var seed = StableHash(link.Id);
                var interval = link.EffectivePollMs * Durations.NsPerMs;
                _entries[link.Id] = new Entry(link, _drivers.Create(link), new DeviceState { LinkId = link.Id }, seed)
                {
                    Order = i,
                    AddedNs = now,
                    // Staggered: each link's first poll at a fixed point of its interval, so a rig of twenty projectors
                    // does not knock on every door in the same tick.
                    NextPollNs = now + (long)(seed % 1000 / 1000.0 * interval / 4),
                };
            }
            foreach (var id in _entries.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                dropped.Add(_entries[id]);
                _entries.Remove(id);
            }
        }
        foreach (var e in dropped)
            await e.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Starts the background loop (polls and liveness every tick).</summary>
    public void Start()
    {
        lock (_gate)
            _loop ??= Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>
    /// Runs one command on a projector through its gate — after any poll or command in flight on it — and asks for a
    /// poll right after, so the published state follows the command. Refused when the link is unknown.
    /// </summary>
    public async Task<CommandResult> RunAsync(string linkId, Func<IProjectorDriver, CancellationToken, Task<CommandResult>> command, CancellationToken ct)
    {
        Entry? e;
        lock (_gate)
            _entries.TryGetValue(linkId, out e);
        if (e is null)
            return CommandResult.Refused("command", $"No projector link '{linkId}'.");
        await e.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await command(e.Driver, ct).ConfigureAwait(false);
        }
        finally
        {
            e.Gate.Release();
            lock (_gate)
                e.NextPollNs = Math.Min(e.NextPollNs, _clock.NowNs);
        }
    }

    /// <summary>A read through the gate (lens, status on demand).</summary>
    public async Task<DriverReading> ReadAsync(string linkId, Func<IProjectorDriver, CancellationToken, Task<DriverReading>> read, CancellationToken ct)
    {
        Entry? e;
        lock (_gate)
            _entries.TryGetValue(linkId, out e);
        if (e is null)
            return new DriverReading(CommandResult.Refused("read", $"No projector link '{linkId}'."), null);
        await e.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var reading = await read(e.Driver, ct).ConfigureAwait(false);
            if (reading.State is { } s && reading.Reached)
                Publish(e, cur => cur.Merge(s) with { Online = true, LastSeenMonoNs = _clock.NowNs, LastProblem = "", Liveness = DeviceLiveness.Live });
            return reading;
        }
        finally
        {
            e.Gate.Release();
        }
    }

    /// <summary>One pass: polls every link that is due (each behind its own gate) and waits for them, then re-judges
    /// liveness. The loop calls it every tick; tests call it with a manual clock.</summary>
    public async Task PollDueAsync(CancellationToken ct)
    {
        List<Entry> due;
        lock (_gate)
        {
            var now = _clock.NowNs;
            due = _entries.Values.Where(e => !e.Polling && e.NextPollNs <= now).ToList();
            foreach (var e in due)
                e.Polling = true;
        }
        await Task.WhenAll(due.Select(e => PollOneAsync(e, ct))).ConfigureAwait(false);
        JudgeLiveness();
    }

    /// <summary>Re-judges every link's liveness from its silence; raises changes.</summary>
    public void JudgeLiveness()
    {
        List<Entry> all;
        lock (_gate)
            all = _entries.Values.ToList();
        var now = _clock.NowNs;
        foreach (var e in all)
            Publish(e, cur => cur with { Liveness = LivenessOf(e, cur, now) });
    }

    private DeviceLiveness LivenessOf(Entry e, DeviceState s, long now)
    {
        if (!e.Attempted)
            return DeviceLiveness.Unknown;
        if (e.RefusedOnly)
            return DeviceLiveness.Unknown;
        var since = s.LastSeenMonoNs > 0 ? s.LastSeenMonoNs : e.AddedNs;
        var silentMs = (now - since) / (double)Durations.NsPerMs;
        var poll = e.Link.EffectivePollMs;
        var staleMs = Math.Max(Core.Bus.ShowBusConstants.StaleMs, _options.StalePolls * poll);
        var goneMs = Math.Max(Core.Bus.ShowBusConstants.GoneMs, _options.GonePolls * poll);
        if (s.LastSeenMonoNs == 0)
            return silentMs < goneMs ? DeviceLiveness.Stale : DeviceLiveness.Gone;
        return silentMs < staleMs ? DeviceLiveness.Live : silentMs < goneMs ? DeviceLiveness.Stale : DeviceLiveness.Gone;
    }

    private async Task PollOneAsync(Entry e, CancellationToken ct)
    {
        try
        {
            await e.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var reading = await e.Driver.ReadStatusAsync(ct).ConfigureAwait(false);
                var now = _clock.NowNs;
                if (reading.Result.Stage == CommandStage.Refused)
                {
                    // A gated driver (unverified, not allowed): nothing is known, nothing failed.
                    e.Attempted = true;
                    e.RefusedOnly = true;
                    ScheduleNext(e, now, failed: false);
                    Publish(e, cur => cur with { Online = false, LastProblem = reading.Result.Words, Liveness = DeviceLiveness.Unknown });
                    return;
                }
                e.RefusedOnly = false;
                e.Attempted = true;
                if (reading.Reached && reading.State is { } s)
                {
                    e.Failures = 0;
                    e.Polls++;
                    var lens = LensPositions.Unknown;
                    var wantLens = (e.Driver.Capabilities & ProjectorCapabilities.LensPositionRead) != 0
                        && ((e.Driver.Unverified & ProjectorCapabilities.LensPositionRead) == 0 || e.Link.AllowUnverified)
                        && (e.Polls == 1 || e.Polls % Math.Max(1, _options.LensEveryPolls) == 0);
                    if (wantLens)
                    {
                        var lensReading = await e.Driver.ReadLensAsync(ct).ConfigureAwait(false);
                        if (lensReading.State is { } ls)
                            lens = ls.Lens;
                    }
                    ScheduleNext(e, now, failed: false);
                    var reached = s with { Lens = lens };
                    Publish(e, cur => cur.Merge(reached) with
                    {
                        Online = true,
                        LastSeenMonoNs = now,
                        LastProblem = "",
                        Liveness = DeviceLiveness.Live,
                    });
                }
                else
                {
                    e.Failures++;
                    ScheduleNext(e, now, failed: true);
                    Publish(e, cur => cur with { Online = false, LastProblem = reading.Result.Words, Liveness = LivenessOf(e, cur, now) });
                }
            }
            finally
            {
                e.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the poll ends with the manager.
        }
        catch (Exception ex)
        {
            // A poll never throws through the loop; the failure is the link's problem in words.
            Publish(e, cur => cur with { Online = false, LastProblem = "poll failed — " + ex.Message });
        }
        finally
        {
            lock (_gate)
                e.Polling = false;
        }
    }

    /// <summary>The next poll: one interval after an answer; after a failure, doubling from one interval to the cap,
    /// with ±10 % deterministic jitter so a rig that lost power together does not come back in lockstep.</summary>
    private void ScheduleNext(Entry e, long now, bool failed)
    {
        var interval = (double)e.Link.EffectivePollMs;
        var ms = failed ? Math.Min(Math.Max(interval, _options.MaxBackoffMs), interval * Math.Pow(2, Math.Min(e.Failures - 1, 10))) : interval;
        var jitter = 0.9 + 0.2 * e.NextJitter();
        lock (_gate)
            e.NextPollNs = now + (long)(ms * jitter * Durations.NsPerMs);
    }

    private void Publish(Entry e, Func<DeviceState, DeviceState> change)
    {
        DeviceState before;
        DeviceState after;
        lock (_gate)
        {
            if (!_entries.TryGetValue(e.Link.Id, out var current) || !ReferenceEquals(current, e))
                return;
            before = e.State;
            after = change(before);
            e.State = after;
        }
        if (before.SameFacts(after))
            return;
        _log.Write($"{e.Link.Id} {e.Link.Name}: {after.Headline()} [{Core.Bus.ShowContext.LightWord(after.Light)}]");
        try
        {
            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs(before, after));
        }
        catch (Exception ex)
        {
            // A subscriber's fault must not stop polling; it is logged as one line.
            _log.Write("device state handler failed — " + ex.Message);
        }
    }

    // ---- PJLink class 2 notifications ----

    /// <summary>Listens for PJLink class 2 notifications on <paramref name="udp"/> (normally UDP 4352): a link-up polls
    /// that projector now; a power, input or error notice is folded into its state as the projector's own report.</summary>
    public void AttachNotifications(PjLinkUdp udp)
    {
        lock (_gate)
        {
            if (_notices is not null)
                _notices.Noticed -= OnNotice;
            _notices = udp;
        }
        udp.Noticed += OnNotice;
        udp.Start();
    }

    private void OnNotice(object? sender, PjLinkNoticeEventArgs e)
    {
        List<Entry> matching;
        lock (_gate)
            matching = _entries.Values.Where(x => HostMatches(x.Link.Host, e.From.Address)).ToList();
        var now = _clock.NowNs;
        foreach (var entry in matching)
        {
            switch (e.Notice.Kind)
            {
                case PjLinkNoticeKind.LinkUp:
                    lock (_gate)
                        entry.NextPollNs = now;
                    break;
                case PjLinkNoticeKind.Power:
                    Publish(entry, cur => cur with { Power = PjLinkProtocol.Power(e.Notice.Value), LastSeenMonoNs = now, Online = true, Liveness = DeviceLiveness.Live });
                    break;
                case PjLinkNoticeKind.Input:
                    Publish(entry, cur => cur with { Input = e.Notice.Value, LastSeenMonoNs = now, Online = true, Liveness = DeviceLiveness.Live });
                    break;
                case PjLinkNoticeKind.Errors when PjLinkProtocol.Faults(e.Notice.Value) is { } faults:
                    Publish(entry, cur => cur with { Faults = faults, FaultsRead = true, LastSeenMonoNs = now, Online = true, Liveness = DeviceLiveness.Live });
                    break;
                default:
                    break;
            }
        }
    }

    private static bool HostMatches(string host, IPAddress from) =>
        IPAddress.TryParse(host, out var a) ? a.Equals(from) || (a.IsIPv4MappedToIPv6 && a.MapToIPv4().Equals(from)) : false;

    // ---- the loop ----

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            List<Entry> due;
            lock (_gate)
            {
                var now = _clock.NowNs;
                due = _entries.Values.Where(e => !e.Polling && e.NextPollNs <= now).ToList();
                foreach (var e in due)
                    e.Polling = true;
            }
            // Each projector polls on its own; a slow one never holds up the others' ticks.
            foreach (var e in due)
                e.PollTask = PollOneAsync(e, ct);
            JudgeLiveness();
            try
            {
                await Task.Delay(_options.TickMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static ulong StableHash(string s)
    {
        // FNV-1a: the same link id staggers and jitters the same way in every run.
        var h = 14695981039346656037UL;
        foreach (var c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        Task? loop;
        PjLinkUdp? notices;
        List<Entry> all;
        lock (_gate)
        {
            loop = _loop;
            notices = _notices;
            _notices = null;
            all = _entries.Values.ToList();
            _entries.Clear();
        }
        if (notices is not null)
            notices.Noticed -= OnNotice;
        if (loop is not null)
            await loop.ConfigureAwait(false);
        foreach (var e in all)
            await e.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private sealed class Entry(ProjectorLink link, IProjectorDriver driver, DeviceState state, ulong seed) : IAsyncDisposable
    {
        private Pcg32 _jitter = new(seed);

        public ProjectorLink Link { get; } = link;

        public IProjectorDriver Driver { get; } = driver;

        /// <summary>One command or poll at a time on this projector.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DeviceState State { get; set; } = state;

        public int Order { get; set; }

        public long AddedNs { get; init; }

        public long NextPollNs { get; set; }

        public int Failures { get; set; }

        public int Polls { get; set; }

        public bool Polling { get; set; }

        public bool Attempted { get; set; }

        public bool RefusedOnly { get; set; }

        public Task? PollTask { get; set; }

        public double NextJitter() => _jitter.NextDouble();

        public async ValueTask DisposeAsync()
        {
            if (PollTask is { } poll)
                await poll.ConfigureAwait(false);
            await Driver.DisposeAsync().ConfigureAwait(false);
            Gate.Dispose();
        }
    }
}
