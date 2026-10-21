using System.Globalization;
using Microsoft.Extensions.Logging;

namespace PlayMap.Engine.Host;

/// <summary>The journal rows of one MEDIA FETCH, bounded whatever its number of files: its pulls' rows (a failed try while a pull tries
/// again, a file landed, a file that could not be had) are journaled as they come until <see cref="Most"/> are, and the rest go to the
/// node's log only; once the fetch has ended, when any went to the log only, one more row counts how it ended and says so. A fetch
/// therefore writes at most <see cref="Most"/> + 1 rows beside its verb's own, and leaves the journal's recent rows, the ones the context
/// and the desk read (<see cref="Journal.RecentCapacity"/>, 200), to the node's other news.</summary>
internal sealed class FetchJournal(Journal journal, ILogger log, string what)
{
    /// <summary>The most rows a fetch's pulls journal: a tenth of the journal's 200 recent rows.</summary>
    public const int Most = 20;

    private int _rows;
    private int _held;
    private int _landed;
    private int _failed;

    /// <summary>A pull's failed try while it tries again.</summary>
    public void Try(string text) => Write("media", text, ok: false);

    /// <summary>A file landed verified.</summary>
    public void Landed(string text)
    {
        Interlocked.Increment(ref _landed);
        Write("media", text, ok: true);
    }

    /// <summary>A file that could not be had, with why.</summary>
    public void Failed(string text)
    {
        Interlocked.Increment(ref _failed);
        Write("refused", text, ok: false);
    }

    /// <summary>The fetch ended: when any of its rows went to the log only, one row says how it ended and how many.</summary>
    public void End()
    {
        var held = Volatile.Read(ref _held);
        if (held == 0)
            return;
        var failed = Volatile.Read(ref _failed);
        journal.Add(failed > 0 ? "refused" : "media", string.Create(CultureInfo.InvariantCulture,
            $"{what} ended: {Volatile.Read(ref _landed)} pulled and verified, {failed} could not be fetched; {held} of its rows are in the node's log only"),
            ok: failed == 0);
    }

    private void Write(string kind, string text, bool ok)
    {
        if (Interlocked.Increment(ref _rows) <= Most)
        {
            journal.Add(kind, text, ok: ok);
            return;
        }
        Interlocked.Increment(ref _held);
        log.LogInformation("Past the fetch's journal rows ({Kind}): {Text}", kind, text);
    }
}
