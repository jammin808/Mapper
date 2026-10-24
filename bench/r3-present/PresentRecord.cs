using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PlayMap.Bench.Present;

/// <summary>One row per present, written as it happens: the path, the frame, whether the quad was white, the QPC ticks just before
/// and just after the present call, the path's own number for the present, the display statistics where the path reads them (the
/// present and sync refresh counts and the sync instant in QPC ticks, DXGI's frame statistics; for the composition swapchain its
/// present id and target instant), and what the call answered. The file's first lines say the QPC frequency and the run's settings,
/// so a photodiode trace and PresentMon's capture line up with it on QPC.</summary>
internal sealed class PresentRecord : IDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly StreamWriter _writer;

    public PresentRecord(string path, string settings)
    {
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        _writer.WriteLine(string.Create(Invariant, $"# PlayMap bench r3-present; QPC frequency {Stopwatch.Frequency} Hz; {settings}"));
        _writer.WriteLine("path,frame,white,before_qpc,after_qpc,present_count,present_refresh,sync_refresh,sync_qpc,answer");
    }

    public int Rows { get; private set; }

    public void Row(string path, long frame, bool white, long before, long after, ulong presentCount, uint presentRefresh,
        uint syncRefresh, long syncQpc, string answer)
    {
        _writer.WriteLine(string.Create(Invariant,
            $"{path},{frame},{(white ? 1 : 0)},{before},{after},{presentCount},{presentRefresh},{syncRefresh},{syncQpc},{answer.Replace(',', ';')}"));
        Rows++;
    }

    /// <summary>A line of words for the record (a path's start, what it was refused), kept with the rows.</summary>
    public void Note(string words) => _writer.WriteLine("# " + words.Replace('\n', ' '));

    public void Dispose() => _writer.Dispose();
}
