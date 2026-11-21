using System.Globalization;
using PlayMap.Core.Machine;
using PlayMap.Core.Time;

namespace PlayMap.PriorityProbe;

/// <summary>The burner: a process that saturates processors the way the product's own work can under its default claims. It claims
/// its class through the product's platform (Windows only: <see cref="WindowsPlatform.Claim"/>, the rest of the settings their
/// defaults) and reads back the class the OS then reports, starts its busy threads at the thread priority a new thread has (as the
/// Compute pool's threads do), writes its ready file once every thread runs, and stops itself at the instant it was given, on the
/// machine's monotonic clock: its control thread waits at the highest thread priority, so its stop is on time and it uses no
/// processor while it waits. On the way it stamps its process's processor time at the victim's window's start and end, each with the
/// instant it read it (a burner ready after the window's start stamps at once, and its stamp says so). Its row, written after the stop,
/// carries the processor time it used from ready to stop and those two stamps. On Windows a thread's processor time is counted in clock
/// ticks, so the process's total moves by whole ticks per thread.</summary>
internal static class Burner
{
    private static int _stop;

    /// <summary>burner --dir D --threads N --class high|normal --from MONO_NS --to MONO_NS --until MONO_NS: exit 0, or 3 when the class it
    /// was told is not the class Windows reads back (its row is written either way). --from and --to are the victim's window.</summary>
    public static int Run(Args args)
    {
        var dir = args.Text("dir");
        var threads = (int)Math.Min(args.Integer("threads"), 1024);
        var cls = args.Word("class", "high", "normal");
        var from = args.Integer("from");
        var to = args.Integer("to");
        var until = args.Integer("until");
        if (from > to || to > until)
            throw new ArgumentException("--from, --to and --until are instants in that order");
        Directory.CreateDirectory(dir);
        Thread.CurrentThread.Priority = ThreadPriority.Highest;

        var (startClass, startBase) = Os.ReadClass();
        string claimWords;
        string claimRead = "";
        string claimApplied = "";
        using var platform = Os.Platform();
        if (OperatingSystem.IsWindows())
        {
            var (priority, words) = WindowsPlatform.Claim(platform, cls);
            claimWords = words;
            claimRead = priority?.ReadBack ?? "";
            claimApplied = priority is { Applied: true } ? "yes" : "no";
        }
        else
        {
            claimWords = "the class claim is Windows only (WindowsMachinePlatform.ClaimProcess): nothing is claimed on " +
                System.Runtime.InteropServices.RuntimeInformation.OSDescription + "; the threads run at the class the process was started at";
        }
        var (readClass, readBase) = Os.ReadClass();
        var expected = cls == "high" ? "High" : "Normal";
        var told = OperatingSystem.IsWindows();
        var classOk = !told || (readClass == expected && claimApplied == "yes");

        var started = 0;
        var workers = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            workers[i] = new Thread(() =>
            {
                Interlocked.Increment(ref started);
                while (Volatile.Read(ref _stop) == 0)
                    Work.Run(20_000);
            })
            { IsBackground = true, Name = "burner " + (i + 1).ToString(CultureInfo.InvariantCulture) };
            workers[i].Start();
        }
        var spin = new SpinWait();
        while (Volatile.Read(ref started) < threads)
            spin.SpinOnce(sleep1Threshold: -1);

        var readyNs = MonoClock.Instance.NowNs;
        var readyUtc = DateTime.UtcNow;
        var cpu0 = Os.ProcessCpuMs();
        Files.WriteAllLines(Path.Combine(dir, "burner.ready"),
            [string.Create(CultureInfo.InvariantCulture, $"{readyNs},{(classOk ? "ok" : "class not read back as told")},{Environment.ProcessId}")]);

        double cpuAtStart;
        long stampStart;
        double cpuAtEnd;
        long stampEnd;
        using (var timer = new RuntimeDeadlineTimer())
        {
            timer.WaitUntil(from);
            stampStart = MonoClock.Instance.NowNs;
            cpuAtStart = Os.ProcessCpuMs();
            timer.WaitUntil(to);
            stampEnd = MonoClock.Instance.NowNs;
            cpuAtEnd = Os.ProcessCpuMs();
            timer.WaitUntil(until);
        }
        var cpu1 = Os.ProcessCpuMs();
        var stopNs = MonoClock.Instance.NowNs;
        Volatile.Write(ref _stop, 1);
        foreach (var w in workers)
            w.Join();

        var wallMs = (stopNs - readyNs) / 1e6;
        Files.WriteCsv(Path.Combine(dir, "burner.csv"), Rows.BurnerHeader,
        [[
            Environment.OSVersion.Platform.ToString(), Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture), threads.ToString(CultureInfo.InvariantCulture), cls,
            startClass, startBase, claimApplied, claimRead, readClass, readBase, told ? (classOk ? "yes" : "no") : "not told", claimWords,
            readyNs.ToString(CultureInfo.InvariantCulture), readyUtc.ToString("o", CultureInfo.InvariantCulture),
            stopNs.ToString(CultureInfo.InvariantCulture), wallMs.ToString("0.0", CultureInfo.InvariantCulture),
            (cpu1 - cpu0).ToString("0.0", CultureInfo.InvariantCulture),
            wallMs > 0 ? ((cpu1 - cpu0) / wallMs).ToString("0.00", CultureInfo.InvariantCulture) : "",
            cpuAtStart.ToString("0.0", CultureInfo.InvariantCulture), stampStart.ToString(CultureInfo.InvariantCulture),
            cpuAtEnd.ToString("0.0", CultureInfo.InvariantCulture), stampEnd.ToString(CultureInfo.InvariantCulture),
        ]]);
        return classOk ? 0 : 3;
    }
}
