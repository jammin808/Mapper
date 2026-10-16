using System.Globalization;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;

namespace PlayMap.Media.Hw;

/// <summary>A source stepped down the decode-path ladder: from which rung to which, on what trigger, and the words.</summary>
public readonly record struct PathStep(string Key, DecodePath From, DecodePath To, DemotionTrigger Trigger, string Why);

/// <summary>The run-time ladder of one clip decoder (zerocopy.md §3.4): the rung it is on, and every trigger that steps it down —
/// an import failure the renderer reported against the source's key, the renderer's device changing under it, a first frame whose
/// planes the renderer does not import, starved waits for a free slot while playing (three over three frame periods in ten
/// seconds; a pause or a seek is no wait), a device that hands the stream back to software. Each step happens once, is raised to
/// <see cref="Stepped"/> with its words, and never goes back up. Decode thread only (<see cref="ShareChanged"/> from any thread);
/// allocation-free except the words of a step.</summary>
public sealed class SurfaceLadder
{
    private readonly StarvationWatch _starvation = new();
    private volatile string? _shareLost;
    private bool _checkedFirst;
    private long _waitSince = -1;

    public SurfaceLadder(string key, IGpuShare? share, DecodePath openedOn, long framePeriodNs)
    {
        Key = key;
        Share = share;
        Path = openedOn;
        FramePeriodNs = framePeriodNs;
        ZeroCopy = share is not null && DecodeLadder.Rank(openedOn) is 1 or 2;
    }

    public string Key { get; }

    /// <summary>The renderer's device the source opened on (null for a source not opened for a renderer).</summary>
    public IGpuShare? Share { get; }

    public long FramePeriodNs { get; set; }

    /// <summary>The rung the source is on now.</summary>
    public DecodePath Path { get; private set; }

    /// <summary>Frames are published as the decoder's own surfaces (until a step down).</summary>
    public bool ZeroCopy { get; private set; }

    /// <summary>The words of every step so far ("" before the first).</summary>
    public string Why { get; private set; } = "";

    public event Action<PathStep>? Stepped;

    /// <summary>The renderer's device the source opened on is no longer the renderer's (its context was rebuilt). Any thread.</summary>
    public void ShareChanged(string words) => _shareLost = words;

    /// <summary>Before the next frame is mapped: false (the source has stepped down) when the renderer reported an import failure
    /// for this source or its device changed.</summary>
    public bool BeforeMap()
    {
        if (!ZeroCopy)
            return false;
        if (_shareLost is { } lost)
        {
            Step(DemotionTrigger.DeviceLost, lost);
            return false;
        }
        if (Share!.ImportFailed(Key, out var words))
        {
            Step(DemotionTrigger.ImportFailed, words);
            return false;
        }
        return true;
    }

    /// <summary>The first frame's planes against what the renderer imports: false (stepped down) when one is not importable.</summary>
    public bool CheckFirst(GpuSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (_checkedFirst || !ZeroCopy)
            return ZeroCopy;
        _checkedFirst = true;
        for (var i = 0; i < surface.PlaneCount; i++)
        {
            var plane = surface.Planes[i];
            if (!Share!.CanImport(surface.Format, plane.Fourcc, plane.Modifier))
            {
                Step(DemotionTrigger.NotImportable, string.Create(CultureInfo.InvariantCulture,
                    $"{DrmFourcc.Name(plane.Fourcc)} with modifier 0x{plane.Modifier:X16}"));
                return false;
            }
        }
        return true;
    }

    /// <summary>No free slot with room in the queue: a wait starts while playing; paused, nothing waits for the renderer.</summary>
    public void NoSlot(long nowNs, bool playing)
    {
        if (!playing)
            _waitSince = -1;
        else if (_waitSince < 0)
            _waitSince = nowNs;
    }

    /// <summary>A slot came: false (stepped down) when the wait that ended was the third starved one in the window.</summary>
    public bool GotSlot(long nowNs)
    {
        if (_waitSince < 0)
            return true;
        var waited = nowNs - _waitSince;
        _waitSince = -1;
        if (!_starvation.Waited(waited, FramePeriodNs, nowNs))
            return true;
        Step(DemotionTrigger.Starved, "");
        return false;
    }

    /// <summary>A seek or a pause: any wait in progress is not a starved one.</summary>
    public void ForgetWait() => _waitSince = -1;

    /// <summary>A frame could not be mapped or read as a surface: the source steps down.</summary>
    public void Failed(string words) => Step(DemotionTrigger.ImportFailed, words);

    /// <summary>The device handed the stream back to software.</summary>
    public void HandedBack(string device) => Step(DemotionTrigger.HandedBack, device);

    private void Step(DemotionTrigger trigger, string detail)
    {
        var from = Path;
        var (to, why) = DecodeLadder.Demote(from, trigger, detail);
        if (to == from)
            return;
        Path = to;
        if (DecodeLadder.Rank(to) >= 3)
            ZeroCopy = false;
        Why = Why.Length == 0 ? why : Why + "; " + why;
        Stepped?.Invoke(new PathStep(Key, from, to, trigger, why));
    }
}
