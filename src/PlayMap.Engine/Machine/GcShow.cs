using System.Collections.Immutable;
using System.Diagnostics.Tracing;
using System.Runtime;
using PlayMap.Core.Machine;

namespace PlayMap.Engine.Machine;

/// <summary>The GC's show mode (MACHINE SET GC SHOW|DEFAULT): under SHOW, arming the show asks the collector for SustainedLowLatency
/// (no blocking full collection while it holds), reads it back and says what it got; disarming puts back the mode it found. Under
/// DEFAULT, the default until the lab measures a cause (L10), it changes nothing and says so. What it holds is published through
/// <see cref="MachineNode.GcHeld"/>.</summary>
internal sealed class GcShowHook(Func<MachineSettings> settings, MachineNode? node) : IShowArmHook
{
    private GCLatencyMode? _before;

    public string Name => "gc";

    public TimeSpan Deadline => TimeSpan.FromSeconds(1);

    public Task<string> ArmingAsync(CancellationToken ct)
    {
        if (settings().Gc != "show")
            return Task.FromResult("the collector keeps its own mode (GC DEFAULT)");
        _before ??= GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        var read = GCSettings.LatencyMode;
        if (node is not null)
            node.GcHeld = read == GCLatencyMode.SustainedLowLatency ? Word(read) : "";
        return Task.FromResult(read == GCLatencyMode.SustainedLowLatency
            ? "the collector holds back blocking full collections while armed (SustainedLowLatency)"
            : $"the runtime kept the collector in {Word(read)}: SustainedLowLatency needs concurrent collection");
    }

    public Task<string> DisarmingAsync(CancellationToken ct)
    {
        if (_before is not { } before)
            return Task.FromResult("");
        GCSettings.LatencyMode = before;
        _before = null;
        if (node is not null)
            node.GcHeld = "";
        return Task.FromResult($"the collector is back in {Word(GCSettings.LatencyMode)}");
    }

    /// <summary>A latency mode's word in STATE: "batch", "interactive", "lowLatency", "sustainedLowLatency", "noGcRegion".</summary>
    public static string Word(GCLatencyMode mode) => mode switch
    {
        GCLatencyMode.Batch => "batch",
        GCLatencyMode.Interactive => "interactive",
        GCLatencyMode.LowLatency => "lowLatency",
        GCLatencyMode.SustainedLowLatency => "sustainedLowLatency",
        GCLatencyMode.NoGCRegion => "noGcRegion",
        _ => "unknown",
    };
}

/// <summary>The collector's pauses as the runtime reports them in-process (EventPipe's <c>Microsoft-Windows-DotNETRuntime</c> GC
/// events: each pause from the execution engine's suspension to its restart), counted into a histogram beside the frame time — the
/// measurement the GC's show mode waits for. Its own listener thread; the summary is read on any thread.</summary>
internal sealed class GcPauses : EventListener
{
    /// <summary>The histogram's upper edges (ms); the last bucket holds every longer pause.</summary>
    public static readonly ImmutableArray<double> EdgesMs = [1, 2, 5, 10, 20, 50];

    private const int Window = 1000;
    private const int SuspendBegin = 9, RestartEnd = 3;
    private readonly Lock _gate = new();
    private readonly int[] _buckets = new int[7];
    private readonly double[] _recent = new double[Window];
    private long _suspendTicks;
    private int _count;
    private int _next;
    private double _maxMs;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // The GC keyword at Informational: the pause events, without the allocation ticks (Verbose).
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(eventSource, EventLevel.Informational, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId == SuspendBegin)
            Interlocked.Exchange(ref _suspendTicks, eventData.TimeStamp.Ticks);
        else if (eventData.EventId == RestartEnd && Interlocked.Exchange(ref _suspendTicks, 0) is var begin and > 0)
            Record((eventData.TimeStamp.Ticks - begin) / (double)TimeSpan.TicksPerMillisecond);
    }

    /// <summary>Counts one pause (ms).</summary>
    public void Record(double ms)
    {
        ms = Math.Max(0, ms);
        lock (_gate)
        {
            var i = 0;
            while (i < EdgesMs.Length && ms >= EdgesMs[i])
                i++;
            _buckets[i]++;
            _recent[_next] = ms;
            _next = (_next + 1) % Window;
            _count++;
            _maxMs = Math.Max(_maxMs, ms);
        }
    }

    /// <summary>The pauses counted, the p99 of the last 1 000, the longest, and each bucket's count.</summary>
    public GcPauseSummary Summary()
    {
        double[] recent;
        lock (_gate)
        {
            if (_count == 0)
                return new GcPauseSummary(0, 0, 0, [.. _buckets]);
            recent = _recent.AsSpan(0, Math.Min(_count, Window)).ToArray();
            Array.Sort(recent);
            var p99 = recent[Math.Min(recent.Length - 1, (int)Math.Ceiling(0.99 * recent.Length) - 1)];
            return new GcPauseSummary(_count, p99, _maxMs, [.. _buckets]);
        }
    }
}

/// <summary>The GC pause histogram: how many pauses, the p99 of the last 1 000 and the longest (ms), and the count under each edge of
/// <see cref="GcPauses.EdgesMs"/> (the last, every longer one).</summary>
public sealed record GcPauseSummary(int Count, double P99Ms, double MaxMs, ImmutableArray<int> Buckets);
