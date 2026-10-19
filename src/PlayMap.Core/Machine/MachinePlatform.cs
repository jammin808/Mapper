using System.Collections.Immutable;
using System.Runtime.Intrinsics;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;

namespace PlayMap.Core.Machine;

/// <summary>What a show node claims from its operating system and what it is: the process's claims made once at start, each
/// thread's claims by role, a deadline timer, keeping the machine awake, the node's identity and capabilities, and the machine's
/// facts for preflight. Every claim answers what it asked, what the system said and what reading it back shows; nothing on the
/// machine changes silently. The engine composes one at start and points <see cref="ThreadClaims"/> at it.</summary>
public interface IMachinePlatform : IDisposable
{
    /// <summary>"windows", or "none" on a node whose platform claims nothing.</summary>
    string Name { get; }

    /// <summary>What the platform is and, when it claims nothing, why.</summary>
    string Words { get; }

    /// <summary>The process's claims under <paramref name="settings"/>, made once at start (and again when a setting changes).</summary>
    ImmutableArray<Claim> ClaimProcess(MachineSettings settings);

    /// <summary>Claims for the calling thread by role; disposing the claim reverts what reverting needs.</summary>
    IThreadClaim ClaimThread(ThreadRole role);

    /// <summary>A deadline timer at the best resolution the platform has; waits are on the machine's monotonic clock.</summary>
    IDeadlineTimer CreateTimer();

    /// <summary>Keeps the display and the system awake while held.</summary>
    IDisposable KeepAwake();

    NodeIdentity Identity();

    CapabilitySnapshot Capabilities();

    /// <summary>The machine's facts as preflight rows read them, each with its light and its words.</summary>
    ImmutableArray<MachineFact> Facts();
}

/// <summary>A wait until an instant on the machine's monotonic clock, for a claimed thread's deadline waits.</summary>
public interface IDeadlineTimer : IDisposable
{
    /// <summary>Waits until <paramref name="monoNs"/> (ns, <see cref="MonoClock"/>) or until <paramref name="cancel"/> is signalled;
    /// false on cancel. An instant already past answers at once.</summary>
    bool WaitUntil(long monoNs, WaitHandle? cancel = null);

    /// <summary>How late the last waits woke after their instant, p99 over the last 1 000 (µs; 0 before any).</summary>
    double LateP99Us { get; }
}

/// <summary>One machine fact as preflight shows it: the item, its light, its value, the words, and the line that fixes it where a
/// verb does (else null, and the words say what a person does).</summary>
public sealed record MachineFact(string Item, Light Light, string Value, string Words, string? Fix = null);

/// <summary>MACHINE SET's settings, one per claim, kept in this node's own settings by its settings writer (never in the project, so a
/// hot backup keeps its own). A claim's setting lands with the claim, with its default and the cause of that default.</summary>
public sealed record MachineSettings
{
    public static MachineSettings Default { get; } = new();
}

/// <summary>The platform of a node that claims nothing from its operating system: every claim is a no-op that says so with
/// <see cref="Words"/>, the timer waits at the runtime's own resolution, and the identity carries this build and what the runtime
/// itself reports (the logical processors, the vector width it accelerates), every other field unread.</summary>
public sealed class NullMachinePlatform : IMachinePlatform
{
    /// <summary>The words of a node whose machine platform has not been composed.</summary>
    public const string NotOnThisNodeWords = "the machine platform is not on this node yet: nothing is claimed from the operating system";

    private readonly IMonoClock _clock;

    public NullMachinePlatform(string words = NotOnThisNodeWords, IMonoClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(words);
        Words = words;
        _clock = clock ?? MonoClock.Instance;
    }

    public string Name => "none";

    public string Words { get; }

    public ImmutableArray<Claim> ClaimProcess(MachineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return [new Claim("process", "", false, "", Words)];
    }

    public IThreadClaim ClaimThread(ThreadRole role) => new UnclaimedThread(role, Words);

    public IDeadlineTimer CreateTimer() => new RuntimeDeadlineTimer(_clock);

    public IDisposable KeepAwake() => new NothingHeld();

    public NodeIdentity Identity() => NodeIdentity.Unread with { Cpu = RuntimeCpu() };

    public CapabilitySnapshot Capabilities() => CapabilitySnapshot.Empty;

    public ImmutableArray<MachineFact> Facts() => [new MachineFact("platform", Light.Grey, Name, Words)];

    public void Dispose()
    {
        // A platform that claimed nothing holds nothing.
    }

    /// <summary>The CPU as the runtime reports it: its logical processors and the widest vector it accelerates; the model and the
    /// efficiency classes are the operating system's, unread here.</summary>
    private static CpuIdentity RuntimeCpu()
    {
        var bits = Vector512.IsHardwareAccelerated ? 512 : Vector256.IsHardwareAccelerated ? 256 : Vector128.IsHardwareAccelerated ? 128 : 0;
        return CpuIdentity.Unknown with { Logical = Environment.ProcessorCount, VectorBits = bits, Avx512 = System.Runtime.Intrinsics.X86.Avx512F.IsSupported };
    }

    private sealed class NothingHeld : IDisposable
    {
        public void Dispose()
        {
            // Nothing was held.
        }
    }
}

/// <summary>A deadline timer on the runtime's own wait (a wait handle's timeout, at the operating system's default resolution): it
/// waits again while it woke before the instant, so it never answers early, however far off the instant is, and records how late
/// each wait woke.</summary>
public sealed class RuntimeDeadlineTimer : IDeadlineTimer
{
    private const int Window = 1000;

    private readonly IMonoClock _clock;
    private readonly ManualResetEvent _never = new(false);
    private readonly double[] _lateUs = new double[Window];
    private readonly Lock _gate = new();
    private int _count;
    private int _next;

    public RuntimeDeadlineTimer(IMonoClock? clock = null)
    {
        _clock = clock ?? MonoClock.Instance;
    }

    public bool WaitUntil(long monoNs, WaitHandle? cancel = null)
    {
        var handle = cancel ?? _never;
        while (true)
        {
            var left = monoNs - _clock.NowNs;
            if (left <= 0)
                break;
            // A wait handle takes at most int.MaxValue ms at once (about 24.8 days): a later instant is waited for in such steps.
            var ms = Math.Min(Math.Ceiling(left / 1e6), int.MaxValue);
            if (handle.WaitOne((int)ms))
                return false;
        }
        if (cancel?.WaitOne(0) == true)
            return false;
        Record((_clock.NowNs - monoNs) / 1e3);
        return true;
    }

    public double LateP99Us
    {
        get
        {
            double[] late;
            lock (_gate)
            {
                if (_count == 0)
                    return 0;
                late = _lateUs.AsSpan(0, _count).ToArray();
            }
            Array.Sort(late);
            return late[Math.Min(late.Length - 1, (int)Math.Ceiling(0.99 * late.Length) - 1)];
        }
    }

    public void Dispose() => _never.Dispose();

    private void Record(double lateUs)
    {
        lock (_gate)
        {
            _lateUs[_next] = Math.Max(0, lateUs);
            _next = (_next + 1) % Window;
            _count = Math.Min(_count + 1, Window);
        }
    }
}
