using System.Collections.Immutable;
using PlayMap.Core.Machine;
using PlayMap.Core.Time;

namespace PlayMap.Engine.Machine;

/// <summary>This node's machine settings (MACHINE SET's claims and MACHINE LOCK's driver set), kept in the node's own settings file by
/// its one writer (the network host) and never in the project, and the runtimes their owners fill into the identity. Any thread.</summary>
public sealed class MachineNode
{
    private readonly Action<MachineSettings> _save;
    private MachineSettings _saved;
    private RuntimeIdentity _runtimes = RuntimeIdentity.Empty;

    public MachineNode(MachineSettings saved, Action<MachineSettings> save)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(save);
        _saved = saved;
        _save = save;
    }

    /// <summary>The settings kept now.</summary>
    public MachineSettings Saved => Volatile.Read(ref _saved);

    /// <summary>Keeps <paramref name="settings"/> (a write that fails throws, and the node keeps what it had).</summary>
    public void Save(MachineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _save(settings);
        Volatile.Write(ref _saved, settings);
        Changed?.Invoke(settings);
    }

    /// <summary>Raised after a save, on the saving thread.</summary>
    public event Action<MachineSettings>? Changed;

    /// <summary>Whether keep-awake is held now (an enabled display output in the project, AWAKE ON).</summary>
    public bool AwakeHeld
    {
        get => Volatile.Read(ref _awakeHeld);
        internal set => Volatile.Write(ref _awakeHeld, value);
    }

    private bool _awakeHeld;

    /// <summary>The collector's mode the GC hook holds while armed ("sustainedLowLatency"), "" when it holds none.</summary>
    public string GcHeld
    {
        get => Volatile.Read(ref _gcHeld);
        internal set => Volatile.Write(ref _gcHeld, value);
    }

    private string _gcHeld = "";

    /// <summary>The collector's pauses, counted while the machine part runs (null before it starts, and on a node without it).</summary>
    public GcPauseSummary? Pauses => _pauses?.Summary();

    private GcPauses? _pauses;

    internal void CountPauses(GcPauses? pauses) => _pauses = pauses;

    /// <summary>The runtimes their owners filled: the Direct3D 12 Agility runtime (wp3-d3d12-core), FFmpeg's build (Media), the sync
    /// board's firmware (wp3-framelock).</summary>
    public RuntimeIdentity Runtimes => Volatile.Read(ref _runtimes);

    /// <summary>An owner fills its runtime ("" leaves the field as it is).</summary>
    public void FillRuntimes(string agility = "", string ffmpeg = "", string syncFirmware = "")
    {
        RuntimeIdentity was, next;
        do
        {
            was = Runtimes;
            next = new RuntimeIdentity(agility.Length > 0 ? agility : was.Agility, ffmpeg.Length > 0 ? ffmpeg : was.Ffmpeg,
                syncFirmware.Length > 0 ? syncFirmware : was.SyncFirmware);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _runtimes, next, was), was));
    }
}

/// <summary>The node's platform as the engine reads it back: every claim made through it (the process's, and each role's latest
/// thread claims) kept with what Windows answered, every deadline wait's lateness counted, and the identity carrying the runtimes their
/// owners filled. It changes nothing the platform does. Any thread.</summary>
public sealed class RecordedPlatform : IMachinePlatform
{
    private const int Window = 1000;

    private readonly IMachinePlatform _inner;
    private readonly IMonoClock _clock;
    private readonly Func<RuntimeIdentity> _runtimes;
    private readonly Lock _gate = new();
    private readonly List<Claim> _threads = [];
    private readonly double[] _lateUs = new double[Window];
    private ImmutableArray<Claim> _process = [];
    private int _next;
    private int _waits;

    public RecordedPlatform(IMachinePlatform inner, IMonoClock clock, Func<RuntimeIdentity> runtimes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(runtimes);
        _inner = inner;
        _clock = clock;
        _runtimes = runtimes;
    }

    /// <summary>The platform underneath.</summary>
    public IMachinePlatform Inner => _inner;

    public string Name => _inner.Name;

    public string Words => _inner.Words;

    public ImmutableArray<Claim> ClaimProcess(MachineSettings settings)
    {
        var claims = _inner.ClaimProcess(settings);
        lock (_gate)
            _process = claims;
        return claims;
    }

    public IThreadClaim ClaimThread(ThreadRole role)
    {
        var claim = _inner.ClaimThread(role);
        lock (_gate)
        {
            _threads.RemoveAll(c => claim.Claims.Any(n => string.Equals(n.What, c.What, StringComparison.Ordinal)));
            _threads.AddRange(claim.Claims);
        }
        return claim;
    }

    public IDeadlineTimer CreateTimer() => new Counted(_inner.CreateTimer(), this);

    public IDisposable KeepAwake() => _inner.KeepAwake();

    public NodeIdentity Identity() => _inner.Identity() with { Runtimes = _runtimes() };

    public CapabilitySnapshot Capabilities() => _inner.Capabilities();

    public ImmutableArray<MachineFact> Facts() => _inner.Facts();

    /// <summary>Every claim made so far: the process's, then each role's latest, in the order they were made.</summary>
    public ImmutableArray<Claim> Claims
    {
        get
        {
            lock (_gate)
                return [.. _process, .. _threads];
        }
    }

    /// <summary>The deadline waits counted (at most the last 1 000) and their p99 lateness (µs; 0 before any).</summary>
    public (int Waits, double LateP99Us) Lateness
    {
        get
        {
            double[] late;
            lock (_gate)
            {
                if (_waits == 0)
                    return (0, 0);
                late = _lateUs.AsSpan(0, _waits).ToArray();
            }
            Array.Sort(late);
            return (late.Length, late[Math.Min(late.Length - 1, (int)Math.Ceiling(0.99 * late.Length) - 1)]);
        }
    }

    private void Record(double lateUs)
    {
        lock (_gate)
        {
            _lateUs[_next] = Math.Max(0, lateUs);
            _next = (_next + 1) % Window;
            _waits = Math.Min(_waits + 1, Window);
        }
    }

    public void Dispose() => _inner.Dispose();

    private sealed class Counted(IDeadlineTimer inner, RecordedPlatform owner) : IDeadlineTimer
    {
        public double LateP99Us => inner.LateP99Us;

        public bool WaitUntil(long monoNs, WaitHandle? cancel = null)
        {
            if (!inner.WaitUntil(monoNs, cancel))
                return false;
            owner.Record((owner._clock.NowNs - monoNs) / 1e3);
            return true;
        }

        public void Dispose() => inner.Dispose();
    }
}
