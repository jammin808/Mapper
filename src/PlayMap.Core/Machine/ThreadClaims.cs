using System.Collections.Immutable;

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
/// state). Disposed on the thread that made it, at that thread's end.</summary>
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

/// <summary>A thread claim that changed nothing: one claim for the role, not applied, with the words why. Disposing it reverts
/// nothing.</summary>
public sealed class UnclaimedThread : IThreadClaim
{
    public UnclaimedThread(ThreadRole role, string words)
    {
        ArgumentNullException.ThrowIfNull(words);
        Role = role;
        var word = ThreadClaims.Word(role);
        Claims = [new Claim("thread." + word, word, false, "", words)];
    }

    public ThreadRole Role { get; }

    public ImmutableArray<Claim> Claims { get; }

    public void Dispose()
    {
        // Nothing was claimed, so nothing reverts.
    }
}
