using System.Collections.Immutable;
using PlayMap.Core.Time;

namespace PlayMap.Core.Machine;

/// <summary>What a thread does for the show, which decides what the machine platform claims for it (its priority, its scheduler
/// class, its CPU set): the render and present threads, decode workers, sound and timecode, the control lane, the LED sender,
/// network servers, background pools and one capture thread per capture input.</summary>
public enum ThreadRole
{
    Render,
    Present,
    Decode,
    Audio,
    Timecode,
    Control,
    Led,
    Network,
    Background,
    Capture,
}

/// <summary>One claim: what was asked, whether the platform applied it, what reading it back shows, and the words why not.
/// <see cref="Applied"/> is the platform's answer and <see cref="ReadBack"/> what was observed after it, so a claim the platform
/// accepted but did not keep is told apart from one it kept.</summary>
public readonly record struct Claim(string What, string Asked, bool Applied, string ReadBack, string Words);

/// <summary>The claims made for one thread; disposing it reverts what reverting needs (a scheduler registration, a throttling
/// state). The claim holds the thread itself, not the thread that disposes it, so any thread may dispose it (at the claimed thread's end,
/// or when its owner lets go).</summary>
public interface IThreadClaim : IDisposable
{
    ImmutableArray<Claim> Claims { get; }
}

/// <summary>Where a thread in Media, Net or the engine claims its role in one line at its start (<c>using var claim =
/// ThreadClaims.Claim(ThreadRole.Decode);</c>), since those projects cannot see the engine's platform. The node's host points it at
/// the platform its services carry once at start (<see cref="Use"/>), before any part starts a thread; until then, and on a node whose
/// platform claims nothing (Core's null platform, until wp3-platform composes its own), a claim changes nothing and its one
/// <see cref="Claim"/> says so (not applied, with the words). Any thread.</summary>
public static class ThreadClaims
{
    /// <summary>The words of a claim made before any platform is in use.</summary>
    public const string NoPlatformWords = "no machine platform is in use yet: nothing was claimed";

    private static IMachinePlatform? _platform;

    /// <summary>The platform claims go to now, or null before <see cref="Use"/>.</summary>
    public static IMachinePlatform? Platform => Volatile.Read(ref _platform);

    /// <summary>Claims the calling thread's role through the platform in use; dispose it at the thread's end.</summary>
    public static IThreadClaim Claim(ThreadRole role) =>
        Platform is { } platform ? platform.ClaimThread(role) : new UnclaimedThread(role, NoPlatformWords);

    /// <summary>Points every claim made from now on at <paramref name="platform"/> (the host's, once at start). A claim made before
    /// keeps what it was.</summary>
    public static void Use(IMachinePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        Volatile.Write(ref _platform, platform);
    }

    /// <summary>The deadline timer of a thread that waits for instants on <paramref name="clock"/> (the LED sender's, the audio feeder's):
    /// the runtime's own wait on <paramref name="clock"/>. Made on the thread that waits on it, which disposes it, since a timer takes one
    /// caller at a time.</summary>
    public static IDeadlineTimer CreateTimer(IMonoClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new RuntimeDeadlineTimer(clock);
    }

    /// <summary>The role's word in STATE and in a claim's <see cref="Claim.What"/>: "render", "present", "decode", "audio",
    /// "timecode", "control", "led", "network", "background", "capture".</summary>
    public static string Word(ThreadRole role) => role switch
    {
        ThreadRole.Render => "render",
        ThreadRole.Present => "present",
        ThreadRole.Decode => "decode",
        ThreadRole.Audio => "audio",
        ThreadRole.Timecode => "timecode",
        ThreadRole.Control => "control",
        ThreadRole.Led => "led",
        ThreadRole.Network => "network",
        ThreadRole.Background => "background",
        ThreadRole.Capture => "capture",
        _ => "unknown",
    };
}

/// <summary>A thread claim that changed nothing: one claim for the role, not applied, with the words why. It asked the role's word
/// where a claim was attempted (no platform in use yet, a thread whose handle could not be taken: a refusal), and nothing ("") where
/// none was (a platform that claims nothing: a grey claim). Disposing it reverts nothing.</summary>
public sealed class UnclaimedThread : IThreadClaim
{
    public UnclaimedThread(ThreadRole role, string words, string? asked = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        Role = role;
        var word = ThreadClaims.Word(role);
        Claims = [new Claim("thread." + word, asked ?? word, false, "", words)];
    }

    public ThreadRole Role { get; }

    public ImmutableArray<Claim> Claims { get; }

    public void Dispose()
    {
        // Nothing was claimed, so nothing reverts.
    }
}

/// <summary>A role claimed by whichever thread delivers a callback, for a listener that owns no thread of its own (an LTC reader fed by
/// the audio hub's capture thread, which fans one device out to every listener): <see cref="Here"/>, called at each delivery, claims
/// the role on the delivering thread the first time, and again when the delivering thread changes (a device reopened on a new thread,
/// told apart even where the new thread takes the old one's id), giving the old claim back first; <see cref="Dispose"/> gives back the
/// claim held. A delivery on the thread already claimed costs one read and allocates nothing. Any thread.</summary>
public sealed class DeliveryClaim : IDisposable
{
    [ThreadStatic]
    private static object? t_self;

    private readonly ThreadRole _role;
    private readonly Func<ThreadRole, IThreadClaim> _claim;
    private readonly Lock _gate = new();
    private IThreadClaim? _held;
    private object? _owner;
    private int _thread;
    private bool _disposed;

    /// <param name="claim">Where the role is claimed (<see cref="ThreadClaims.Claim"/> unless a test says otherwise).</param>
    public DeliveryClaim(ThreadRole role, Func<ThreadRole, IThreadClaim>? claim = null)
    {
        _role = role;
        _claim = claim ?? ThreadClaims.Claim;
    }

    /// <summary>The managed id of the thread whose claim is held, or 0 when none is.</summary>
    public int Thread => Volatile.Read(ref _thread);

    /// <summary>The claim held now, or null.</summary>
    public IThreadClaim? Held
    {
        get
        {
            lock (_gate)
                return _held;
        }
    }

    /// <summary>One object per thread for its life: a thread's identity that no later thread shares.</summary>
    private static object Self => t_self ??= new object();

    /// <summary>Called on the delivering thread at each delivery: claims the role here unless this thread's claim is the one held.</summary>
    public void Here()
    {
        var self = Self;
        if (ReferenceEquals(Volatile.Read(ref _owner), self))
            return;
        IThreadClaim? old;
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(_owner, self))
                return;
            (old, _held) = (_held, null);
            Volatile.Write(ref _owner, null);
            Volatile.Write(ref _thread, 0);
        }
        // The old thread's claim goes back first, so a thread that took over a finished thread's id never joins its stale claim.
        old?.Dispose();
        var made = _claim(_role);
        lock (_gate)
        {
            if (!_disposed)
            {
                _held = made;
                Volatile.Write(ref _thread, Environment.CurrentManagedThreadId);
                Volatile.Write(ref _owner, self);
                return;
            }
        }
        made.Dispose();
    }

    public void Dispose()
    {
        IThreadClaim? held;
        lock (_gate)
        {
            _disposed = true;
            (held, _held) = (_held, null);
            Volatile.Write(ref _owner, null);
            Volatile.Write(ref _thread, 0);
        }
        held?.Dispose();
    }
}
