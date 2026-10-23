using System.Globalization;
using PlayMap.Core.Time;

namespace PlayMap.Net.Media;

/// <summary>The silence of one body a peer sends, as a clock counts it: each read is marked as it starts (<see cref="Reading"/>) and as
/// it returns (<see cref="Heard"/>), so only the time a read waits for the peer is counted, never the time between reads. A watch
/// steps on the runtime's timer (<see cref="Step"/>) and cuts the read (<see cref="Token"/>) once one has waited the quiet on the clock;
/// on a clock a test moves by hand no silence is counted until the test moves it. Disposing it stops the watch. A content-addressed
/// pull's chunks (<see cref="MediaTransfer"/>) and a backup's show files (<see cref="Fleet.MirrorPuller.ShowFileAsync"/>) read their
/// bodies through it.</summary>
internal sealed class BodySilence : IAsyncDisposable
{
    private const long NotReading = long.MinValue;
    private readonly IMonoClock _clock;
    private readonly TimeSpan _quiet;
    private readonly long _quietNs;
    private readonly CancellationTokenSource _cut;
    private readonly CancellationTokenSource _done = new();
    private readonly Task _watch;
    private long _sinceNs = NotReading;
    private int _met;

    public BodySilence(IMonoClock clock, TimeSpan quiet, CancellationToken ct)
    {
        _clock = clock;
        _quiet = quiet;
        _quietNs = quiet.Ticks * TimeSpan.NanosecondsPerTick;
        _cut = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _watch = WatchAsync(Step(quiet));
    }

    /// <summary>How often the silence is looked at: an eighth of the quiet, from 20 to 250 ms, so a stall is called at most a step after
    /// the quiet has passed on the clock.</summary>
    private static TimeSpan Step(TimeSpan quiet) => TimeSpan.FromMilliseconds(Math.Clamp(quiet.TotalMilliseconds / 8, 20, 250));

    /// <summary>Cancelled when a read has waited the quiet, or with the pull.</summary>
    public CancellationToken Token => _cut.Token;

    /// <summary>The quiet was met: a read waited it out.</summary>
    public bool Met => Volatile.Read(ref _met) != 0;

    public void Reading() => Interlocked.Exchange(ref _sinceNs, _clock.NowNs);

    public void Heard() => Interlocked.Exchange(ref _sinceNs, NotReading);

    /// <summary>What the peer did, once the quiet was met: "sent nothing for 10 s, 65536 of 3145745 bytes in" (without "of …" when the
    /// body's length is not known).</summary>
    public string Words(long bytesIn, long? length) => length is { } of
        ? string.Create(CultureInfo.InvariantCulture, $"sent nothing for {Seconds(_quiet)}, {bytesIn} of {of} bytes in")
        : string.Create(CultureInfo.InvariantCulture, $"sent nothing for {Seconds(_quiet)}, {bytesIn} bytes in");

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";

    private async Task WatchAsync(TimeSpan step)
    {
        while (await Waits.DelayAsync(step, _done.Token).ConfigureAwait(false))
        {
            var since = Interlocked.Read(ref _sinceNs);
            if (since == NotReading || _clock.NowNs - since < _quietNs)
                continue;
            Volatile.Write(ref _met, 1);
            await _cut.CancelAsync().ConfigureAwait(false);
            return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _done.CancelAsync().ConfigureAwait(false);
        await _watch.ConfigureAwait(false);
        _done.Dispose();
        _cut.Dispose();
    }
}
