using System.Collections.Immutable;
using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Core.Wire;
using PlayMap.Render.Platform;

namespace PlayMap.Engine;

// The seams round 3's packages implement and call across each other. Each has a null implementation that answers with words why
// nothing happens, and EngineServices carries every one at its null implementation (the observer, the preview share, the GPU
// lifecycle listeners, the machine platform, the arm hooks), so a node composes each seam before the package that fills it merges:
// that package's extension sets it in its Configure. The machine platform's seam (IMachinePlatform) lives in Core
// (PlayMap.Core.Machine): Media's and Net's threads claim their roles through Core's ThreadClaims, which the host points at the
// composed platform once at start, and the Windows platform project references Core alone.

/// <summary>An observer's latest reading of one output's air: the verdict's word ("ok", "held", "black", "stale", "torn", "wrong",
/// "late", "lost") and its light, the counter the marks carried, how many frames it has decoded (−1: unknown), the instant it
/// captured them on the machine's monotonic clock (ns), how late they lit against the predicted display time (ms), and the words.</summary>
public readonly record struct AirObservation(string OutputId, string Verdict, Light Light, long Counter, long ObservedFrames, long CapturedNs,
    double LateMs, string Words);

/// <summary>What an independent observer saw of the air, as every package reads it (HEALTH's observed frames, a pass-through output's
/// glass-to-glass step, a rebuild's first good frame): published readings only; any thread.</summary>
public interface IAirObserver
{
    /// <summary>Whether an observer is running on this node.</summary>
    bool Running { get; }

    /// <summary>What the observer is doing and, when it is not running, why.</summary>
    string Words { get; }

    /// <summary>The latest reading of an output, or null when it is not observed.</summary>
    AirObservation? Observation(string outputId);

    /// <summary>Raised, on any thread, for each reading as it comes in.</summary>
    event Action<AirObservation>? Observed;
}

/// <summary>A node with no observer: no output is observed, and the words say why.</summary>
public sealed class NoAirObserver : IAirObserver
{
    public static readonly NoAirObserver Instance = new();

    private NoAirObserver()
    {
    }

    public bool Running => false;

    public string Words => "the air observer is not on this node yet: no output is observed";

    public AirObservation? Observation(string outputId)
    {
        ArgumentNullException.ThrowIfNull(outputId);
        return null;
    }

    public event Action<AirObservation>? Observed
    {
        add
        {
            // Nothing is ever observed, so a listener is never called.
        }
        remove
        {
            // As above: no listener is kept.
        }
    }
}

/// <summary>A lost GPU device as the node recorded it before releasing anything: the device epoch that was lost, the reason the
/// device gave, the driver, the last pass the GPU completed and the first it did not (from the breadcrumbs, "" where none), a page
/// fault's words ("none"), where a vendor's crash dump was written ("" with none), and when.</summary>
public readonly record struct GpuIncident(long Epoch, string Reason, string Driver, string LastCompleted, string FirstOutstanding, string PageFault,
    string Dump, long AtUnixMs);

/// <summary>Told when the render device is lost and when it stands again: before any of its objects is released, and after the new
/// ones exist, so a presenter, a decoder's hardware device, the upload arena, a preview's share or a barrier registration lets go and
/// is made again on the new epoch. The rebuild is the start, run again: the same steps in the same order. Render thread.</summary>
public interface IGpuLifecycle
{
    /// <summary>Before the device of <paramref name="epoch"/> releases its objects.</summary>
    void Releasing(long epoch, in GpuIncident incident);

    /// <summary>After the device of <paramref name="epoch"/> (the lost one's + 1) stands on <paramref name="platform"/>.</summary>
    void Rebuilt(long epoch, IGpuPlatform platform);
}

/// <summary>A listener that holds nothing on the device, so a loss asks nothing of it.</summary>
public sealed class NoGpuLifecycle : IGpuLifecycle
{
    public static readonly NoGpuLifecycle Instance = new();

    private NoGpuLifecycle()
    {
    }

    public static string Words => "nothing on this node is made again with the render device yet";

    public void Releasing(long epoch, in GpuIncident incident)
    {
        // It holds nothing on the device.
    }

    public void Rebuilt(long epoch, IGpuPlatform platform) => ArgumentNullException.ThrowIfNull(platform);
}

/// <summary>A preview view shared into a desk's process: the adapter it lives on, the device epoch, its size and format, and the
/// handles of its three slots and two fences — values that mean something in that process only.</summary>
public sealed record SharedPreview(string ViewId, long AdapterLuid, long Epoch, int Width, int Height, string Format, ImmutableArray<long> Slots,
    long Ready, long Released);

/// <summary>A preview drawn into textures shared with a local desk instead of the shared-memory ring (<c>VIEW … SHARED &lt;pid&gt;</c>).
/// The engine never waits on the desk: a slot the desk has not released is skipped, as the ring skips one.</summary>
public interface IPreviewShare
{
    /// <summary>What sharing does on this node and, where it does not, why (the view keeps its ring).</summary>
    string Words { get; }

    /// <summary>Shares a view at its displayed size into process <paramref name="pid"/>, which must own <paramref name="session"/>'s
    /// loopback connection and run as the engine's own user in its own session; null, with <paramref name="words"/>, when refused.</summary>
    SharedPreview? Share(string viewId, int width, int height, int pid, SessionInfo session, out string words);

    /// <summary>Closes a view's handles and releases its textures after their last fence value; false when it was not shared.</summary>
    bool Release(string viewId);
}

/// <summary>A node whose previews all go through the ring: every share is refused with the words why.</summary>
public sealed class NoPreviewShare : IPreviewShare
{
    public static readonly NoPreviewShare Instance = new();

    private NoPreviewShare()
    {
    }

    public string Words => "shared previews are not on this node yet: every view goes through its ring";

    public SharedPreview? Share(string viewId, int width, int height, int pid, SessionInfo session, out string words)
    {
        ArgumentNullException.ThrowIfNull(viewId);
        ArgumentNullException.ThrowIfNull(session);
        words = Words;
        return null;
    }

    public bool Release(string viewId)
    {
        ArgumentNullException.ThrowIfNull(viewId);
        return false;
    }
}

/// <summary>Work a package does as the show arms (plans compiled, pipelines warmed, the collector's mode) and as it disarms.
/// <c>SHOW ARM</c> runs every registered hook before it answers and names what each did; a hook past its <see cref="Deadline"/> is
/// cancelled and named with words, and the show arms either way (armed is the safe side). A line from the sync leader is answered
/// before its hooks end, each outcome journalled; a node that comes back armed after a restart runs <see cref="ArmingAsync"/> once,
/// when it is up, each outcome journalled as a recovery row. Asynchronous; never on the render thread.</summary>
public interface IShowArmHook
{
    /// <summary>The hook's name in the answer ("plans", "pipelines", "gc").</summary>
    string Name { get; }

    /// <summary>How long arming waits for this hook before cancelling it: at most <see cref="EngineCore.MaxArmHookDeadline"/>, which
    /// an infinite deadline (<see cref="Timeout.InfiniteTimeSpan"/>) or a longer one means; a negative one means none.</summary>
    TimeSpan Deadline { get; }

    /// <summary>Runs as the show arms; answers what it did, in words. <paramref name="ct"/> is cancelled at the deadline.</summary>
    Task<string> ArmingAsync(CancellationToken ct);

    /// <summary>Runs as the show disarms; answers what it did ("" when it does nothing then).</summary>
    Task<string> DisarmingAsync(CancellationToken ct) => Task.FromResult("");
}

/// <summary>Round 3's seams a node is composed with, each at its null implementation until a package's extension sets it in its
/// Configure: the air observer, the preview share, the GPU lifecycle listeners, the machine platform, and the arm hooks with the
/// timers their deadlines run on.</summary>
public sealed partial record EngineServices
{
    /// <summary>What an independent observer saw of the air (wp3-stability sets it).</summary>
    public IAirObserver Observer { get; init; } = NoAirObserver.Instance;

    /// <summary>Previews shared with a local desk without a CPU copy (wp3-desk-gpu sets it).</summary>
    public IPreviewShare PreviewShare { get; init; } = NoPreviewShare.Instance;

    /// <summary>Every listener told when the render device is lost and stands again, in order: a package adds its own (wp3-present,
    /// wp3-video, wp3-desk-gpu, wp3-framelock); the render host calls them (wp3-stability-2).</summary>
    public ImmutableArray<IGpuLifecycle> GpuLifecycles { get; init; } = [];

    /// <summary>What the node claims from its operating system (wp3-platform sets it). The host points <see cref="ThreadClaims"/> at
    /// it once at start, before any part starts a thread.</summary>
    public IMachinePlatform MachinePlatform { get; init; } = new NullMachinePlatform();

    /// <summary>The hooks SHOW ARM and SHOW DISARM run before they answer, each named in the answer (none: the mode's answer alone).</summary>
    public ImmutableArray<IShowArmHook> ArmHooks { get; init; } = [];

    /// <summary>The timers an arm hook's deadline runs on: the system's, or a test's clock moved by hand.</summary>
    public TimeProvider ArmTimers { get; init; } = TimeProvider.System;
}

/// <summary>A hook with nothing to prepare: it answers at once, saying so.</summary>
public sealed class NothingToArm : IShowArmHook
{
    public static readonly NothingToArm Instance = new();

    private NothingToArm()
    {
    }

    public string Name => "none";

    public TimeSpan Deadline => TimeSpan.Zero;

    public Task<string> ArmingAsync(CancellationToken ct) => Task.FromResult("nothing is prepared at arm on this node yet");
}
