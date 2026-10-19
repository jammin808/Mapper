using System.Globalization;

namespace PlayMap.App.Controls;

/// <summary>How old a preview's picture is, read on the desk's clock from the pictures as they arrive: its own frame rate from its ring
/// slots' stamps (the node's clock: only their differences are read), its age from when the newest picture came. Older than two frames
/// of its own rate it is stale (a badge with the age); with no new picture for a second it is frozen (the picture greyed with the reason:
/// the ring gone, the engine quiet, or no new picture). A preview that has had no picture yet has no age.</summary>
public sealed class PreviewAge
{
    /// <summary>A picture this long without a successor is frozen.</summary>
    public const long FrozenMs = 1000;

    private long _lastStampNs = long.MinValue;
    private long _arrivedMs = long.MinValue;
    private double _periodMs;

    /// <summary>The frame period read from the stamps (0 until two pictures came).</summary>
    public double PeriodMs => _periodMs;

    /// <summary>A new picture came at <paramref name="nowMs"/> on the desk's clock, stamped <paramref name="stampNs"/> by the node.</summary>
    public void Arrived(long stampNs, long nowMs)
    {
        if (_lastStampNs != long.MinValue && stampNs > _lastStampNs)
        {
            var period = (stampNs - _lastStampNs) / 1e6;
            // The newest interval leads; a short hiccup moves the period by a quarter of itself.
            _periodMs = _periodMs <= 0 ? period : (_periodMs * 0.75) + (period * 0.25);
        }
        _lastStampNs = stampNs;
        _arrivedMs = nowMs;
    }

    /// <summary>The picture's age now (null before the first picture).</summary>
    public long? AgeMs(long nowMs) => _arrivedMs == long.MinValue ? null : Math.Max(0, nowMs - _arrivedMs);

    /// <summary>Older than two frames of its own rate.</summary>
    public bool IsStale(long nowMs) => AgeMs(nowMs) is { } age && _periodMs > 0 && age > 2 * _periodMs;

    /// <summary>No new picture for <see cref="FrozenMs"/>.</summary>
    public bool IsFrozen(long nowMs) => AgeMs(nowMs) is >= FrozenMs;

    /// <summary>"stale 120 ms" while stale, else null.</summary>
    public string? Badge(long nowMs) => IsStale(nowMs) && AgeMs(nowMs) is { } age
        ? age >= FrozenMs ? string.Create(CultureInfo.InvariantCulture, $"stale {age / 1000.0:0.0} s") : string.Create(CultureInfo.InvariantCulture, $"stale {age} ms")
        : null;

    /// <summary>Why a frozen picture is frozen: the ring the node named is gone, the engine is quiet (the link is not live), or the ring
    /// brings no new picture.</summary>
    public string? FrozenWords(long nowMs, bool ringLive, bool engineLive)
    {
        if (!IsFrozen(nowMs) || AgeMs(nowMs) is not { } age)
            return null;
        var seconds = string.Create(CultureInfo.InvariantCulture, $"{age / 1000.0:0.0} s");
        if (!ringLive)
            return $"Frozen {seconds}: the preview's ring is gone";
        return engineLive ? $"Frozen {seconds}: the ring brings no new picture" : $"Frozen {seconds}: the engine is quiet";
    }

    /// <summary>Forgets the pictures (another ring, another view).</summary>
    public void Reset()
    {
        _lastStampNs = long.MinValue;
        _arrivedMs = long.MinValue;
        _periodMs = 0;
    }
}
