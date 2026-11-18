using System.Diagnostics;
using System.Globalization;
using PlayMap.Core.Time;

namespace PlayMap.PriorityProbe;

/// <summary>What a saturating process costs a Normal-class process doing real work, at the burner's HIGH and at its NORMAL, in legs.
/// A measurement for the owner to read, never a pass mark: a run fails only when a leg wrote no files, a process did not start or did
/// not read back the class it was told, or a leg ran past its deadline.
///   calibrate --out ROOT                   the frame's fixed work, about 10 ms of wall time on the quiet machine, to ROOT/calibration.csv
///   leg --out ROOT --name NAME [--seconds S] [--lead L]   one leg (the names: <see cref="Legs.All"/>) into ROOT/leg-NAME
///   read --out ROOT                        the table of every leg present, to ROOT/summary.md and ROOT/summary.csv, and printed
///   legs --out ROOT [--seconds S] [--lead L]   calibrate, every leg in order, then read (a local run; CI runs the steps one by one)
///   burner ..., victim ...                 the two processes a leg starts (never run by hand)</summary>
internal static class Program
{
    public const double DefaultSeconds = 20;
    public const double DefaultLead = 3;

    private static readonly StreamWriter Out = new(Console.OpenStandardOutput()) { AutoFlush = true };

    public static void Say(string line) => Out.WriteLine(line);

    private static async Task<int> Main(string[] argv)
    {
        if (argv.Length == 0)
        {
            Say("say a mode: calibrate, leg, read or legs (docs: README, Build; docs/round2/CARRYOVER.md, fix-ci-priority-measure)");
            return 2;
        }
        try
        {
            var args = new Args(argv.Skip(1));
            return argv[0] switch
            {
                "calibrate" => Calibration.Run(args.Text("out")),
                "leg" => await Legs.RunAsync(args.Text("out"), Legs.Find(args.Text("name")), Seconds(args), Lead(args)),
                "read" => Reader.Run(args.Text("out")),
                "legs" => await Legs.AllAsync(args.Text("out"), Seconds(args), Lead(args)),
                "burner" => Burner.Run(args),
                "victim" => Victim.Run(args),
                _ => throw new ArgumentException($"'{argv[0]}' is not a mode: calibrate, leg, read or legs"),
            };
        }
        catch (ArgumentException e)
        {
            Say("refused: " + e.Message);
            return 2;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or FormatException
                                      or System.ComponentModel.Win32Exception or OverflowException)
        {
            // A leg's children were stopped on the way out (Child.Dispose), so no burner outlives the run that started it.
            Say("failed: " + e.GetType().Name + ": " + e.Message);
            return 1;
        }
    }

    private static double Seconds(Args args) => args.Has("seconds") ? args.Number("seconds") : DefaultSeconds;

    private static double Lead(Args args) => args.Has("lead") ? args.Number("lead") : DefaultLead;
}

/// <summary>The frame's fixed work: iterations of <see cref="Work.Run"/> made about 10 ms of wall time on the machine as it is when
/// calibrated (the quiet runner, before any leg), so every leg's frames do the same work and only its wall time can grow.</summary>
internal static class Calibration
{
    public const double TargetMs = 10;
    public const string File = "calibration.csv";

    public static int Run(string root)
    {
        Directory.CreateDirectory(root);
        for (var i = 0; i < 20; i++)
            Work.Run(100_000);
        var iterations = 1_000_000L;
        for (var round = 0; round < 6; round++)
        {
            var median = Measure(iterations, 5)[2];
            iterations = Math.Max(1_000, (long)(iterations * TargetMs / Math.Max(0.01, median)));
        }
        var samples = Measure(iterations, 31);
        Files.WriteCsv(Path.Combine(root, File), Rows.CalibrationHeader,
        [[
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture), iterations.ToString(CultureInfo.InvariantCulture),
            TargetMs.ToString("0.0", CultureInfo.InvariantCulture), Fmt(samples[15]), Fmt(samples[3]), Fmt(samples[27]),
            samples.Length.ToString(CultureInfo.InvariantCulture),
        ]]);
        Program.Say($"calibration: {iterations} iterations, median {Fmt(samples[15])} ms (p10 {Fmt(samples[3])}, p90 {Fmt(samples[27])}) on {Environment.ProcessorCount} processors");
        return 0;
    }

    /// <summary>The iterations the calibration wrote, or null when there is none.</summary>
    public static long? Read(string root)
    {
        var rows = Files.ReadCsv(Path.Combine(root, File));
        return rows.Count == 1 && long.TryParse(rows[0]["iterations"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    private static double[] Measure(long iterations, int count)
    {
        var ms = new double[count];
        for (var i = 0; i < count; i++)
        {
            var t0 = MonoClock.Instance.NowNs;
            Work.Run(iterations);
            ms[i] = (MonoClock.Instance.NowNs - t0) / 1e6;
        }
        Array.Sort(ms);
        return ms;
    }

    private static string Fmt(double ms) => ms.ToString("0.000", CultureInfo.InvariantCulture);
}

/// <summary>One leg: what burns beside the victim (nothing in the baseline), how many threads, the class the burner claims, and the
/// victim's class (Normal set by its own hand, or kept as the runner started it).</summary>
internal sealed record LegSpec(string Name, string Label, int Threads, string BurnerClass, string VictimClass);

internal static class Legs
{
    /// <summary>The processor count less one, at least one: the width the product's Compute pool (src/PlayMap.Core/Threading/Compute.cs
    /// 102) and its HAP workers (src/PlayMap.Media/Hap/HapWorkers.cs 92) take.</summary>
    public static int CoresLessOne => Math.Max(1, Environment.ProcessorCount - 1);

    public static IReadOnlyList<LegSpec> All =>
    [
        new("baseline", "no burner; the victim at Normal", 0, "", "normal"),
        new("high-n", $"the burner HIGH on every processor ({Environment.ProcessorCount} threads); the victim at Normal", Environment.ProcessorCount, "high", "normal"),
        new("normal-n", $"the burner NORMAL on every processor ({Environment.ProcessorCount} threads); the victim at Normal", Environment.ProcessorCount, "normal", "normal"),
        new("high-n1", $"the burner HIGH on cores less one ({CoresLessOne} threads); the victim at Normal", CoresLessOne, "high", "normal"),
        new("normal-n1", $"the burner NORMAL on cores less one ({CoresLessOne} threads); the victim at Normal", CoresLessOne, "normal", "normal"),
        new("high-n-runner", $"the burner HIGH on every processor ({Environment.ProcessorCount} threads); the victim at the runner's own class (the runner's condition, not a desk's)", Environment.ProcessorCount, "high", "keep"),
    ];

    public static LegSpec Find(string name) =>
        All.FirstOrDefault(l => l.Name == name) ?? throw new ArgumentException($"'{name}' is not a leg: {string.Join(", ", All.Select(l => l.Name))}");

    public const double ReadyBoundSeconds = 60;
    public const double AfterWindowBoundSeconds = 90;

    public static async Task<int> AllAsync(string root, double seconds, double lead)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException($"{root} is not empty: the legs are written into a folder of their own");
        var broken = Calibration.Run(root) != 0;
        foreach (var leg in All)
            broken |= await RunAsync(root, leg, seconds, lead) != 0;
        Reader.Run(root);
        return broken ? 1 : 0;
    }

    /// <summary>Runs one leg: the victim started and ready, then the burner started into the victim's window and told to stop a second
    /// after it, both waited for under the leg's deadline. Writes the leg's row; 0, or 1 when the leg is broken (the words say why).</summary>
    public static async Task<int> RunAsync(string root, LegSpec leg, double seconds, double lead)
    {
        if (seconds <= 0)
            throw new ArgumentException("--seconds takes a number above zero");
        var dir = Path.Combine(root, "leg-" + leg.Name);
        Directory.CreateDirectory(dir);
        if (new[] { "victim.ready", "burner.ready", "victim.csv", "burner.csv", "leg.csv" }.Any(f => File.Exists(Path.Combine(dir, f))))
            throw new ArgumentException($"{dir} holds a leg's files already: a leg is written into a folder of its own");
        var started = DateTime.UtcNow;
        var problems = new List<string>();
        var victimExit = "";
        var burnerExit = "";
        var victimReadyUtc = "";
        var burnerReadyUtc = "";
        Program.Say($"leg {leg.Name}: {leg.Label}, {seconds.ToString("0.#", CultureInfo.InvariantCulture)} s");

        var work = Calibration.Read(root);
        if (work is null)
        {
            problems.Add("no calibration: run calibrate first");
        }
        else
        {
            var deadline = Stopwatch.StartNew();
            var bound = TimeSpan.FromSeconds(ReadyBoundSeconds + lead + seconds + AfterWindowBoundSeconds);
            using var victim = Child.Start(dir, "victim", ["--dir", dir, "--seconds", Num(seconds), "--lead", Num(lead), "--work", Num(work.Value), "--class", leg.VictimClass]);
            Child? burner = null;
            try
            {
                if (victim is null)
                {
                    problems.Add("the victim did not start");
                }
                else
                {
                    var ready = await WaitForFileAsync(Path.Combine(dir, "victim.ready"), victim, TimeSpan.FromSeconds(ReadyBoundSeconds));
                    if (ready is null)
                    {
                        problems.Add($"the victim wrote no ready file within {ReadyBoundSeconds} s");
                    }
                    else
                    {
                        victimReadyUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                        var cells = ready.Split(',');
                        var end = long.Parse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture);
                        if (leg.Threads > 0)
                        {
                            // The burner stops itself a second after the victim's window, on the machine's monotonic clock: nothing waits on
                            // this controller, whose own class is the runner's and which a HIGH burner can hold.
                            burner = Child.Start(dir, "burner", ["--dir", dir, "--threads", Num(leg.Threads), "--class", leg.BurnerClass, "--until", Num(end + 1_000_000_000L)]);
                            if (burner is null)
                            {
                                problems.Add("the burner did not start");
                            }
                            else
                            {
                                var burnerReady = await WaitForFileAsync(Path.Combine(dir, "burner.ready"), burner, TimeSpan.FromSeconds(ReadyBoundSeconds));
                                if (burnerReady is null)
                                    problems.Add($"the burner wrote no ready file within {ReadyBoundSeconds} s");
                                else
                                {
                                    burnerReadyUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                                    var said = burnerReady.Split(',');
                                    if (said.Length > 1 && said[1] != "ok")
                                        problems.Add("the burner: " + said[1]);
                                    if (long.Parse(said[0], NumberStyles.Integer, CultureInfo.InvariantCulture) >= end)
                                        problems.Add("the burner was ready only after the victim's window had ended");
                                }
                            }
                        }
                    }
                }
                var left = bound - deadline.Elapsed;
                if (victim is not null)
                    victimExit = await victim.EndAsync(left, problems);
                if (burner is not null)
                    burnerExit = await burner.EndAsync(bound - deadline.Elapsed, problems);
            }
            finally
            {
                burner?.Dispose();
            }
            if (victimExit == "3")
                problems.Add("the victim was told Normal and Windows reads back another class");
            else if (victimExit is not "" and not "0")
                problems.Add($"the victim ended with exit {victimExit}");
            if (burnerExit == "3")
                problems.Add("the burner was told its class and Windows reads back another");
            else if (burnerExit is not "" and not "0")
                problems.Add($"the burner ended with exit {burnerExit}");
            foreach (var file in leg.Threads > 0 ? new[] { "victim.csv", "frames.csv", "ticks.csv", "pool.csv", "burner.csv" } : ["victim.csv", "frames.csv", "ticks.csv", "pool.csv"])
            {
                if (!File.Exists(Path.Combine(dir, file)))
                    problems.Add($"no {file}");
            }
        }

        var outcome = problems.Count == 0 ? "ok" : "broken";
        Files.WriteCsv(Path.Combine(dir, "leg.csv"), Rows.LegHeader,
        [[
            leg.Name, leg.Label, Num(leg.Threads), leg.BurnerClass, leg.VictimClass, Num(seconds), Num(lead),
            started.ToString("o", CultureInfo.InvariantCulture), victimReadyUtc, burnerReadyUtc, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            victimExit, burnerExit, outcome, string.Join("; ", problems),
        ]]);
        Program.Say($"leg {leg.Name}: {outcome}{(problems.Count > 0 ? ": " + string.Join("; ", problems) : "")}");
        return problems.Count == 0 ? 0 : 1;
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>The first line of a ready file once it is there and whole, or null when the process ended or the bound ran out first.</summary>
    private static async Task<string?> WaitForFileAsync(string path, Child child, TimeSpan bound)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < bound)
        {
            if (File.Exists(path))
            {
                try
                {
                    var line = File.ReadAllLines(path).FirstOrDefault();
                    if (line is not null && line.Split(',').Length >= 3)
                        return line;
                }
                catch (IOException)
                {
                    // Read while its writer still holds it: read again at the next look.
                }
            }
            if (child.Exited)
                return null;
            await Task.Delay(50);
        }
        return null;
    }
}

/// <summary>A process of the probe's own (this program in another mode), its output and errors kept in its leg's folder.</summary>
internal sealed class Child : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly Lock _gate = new();
    private readonly string _role;
    private bool _closed;

    private Child(Process process, StreamWriter log, string role)
    {
        _process = process;
        _log = log;
        _role = role;
    }

    public bool Exited => _process.HasExited;

    public static Child? Start(string dir, string role, string[] args)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("the runtime names no process path");
        var info = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        // Run as `dotnet PlayMap.PriorityProbe.dll`, the process is dotnet and the program its first argument; run as its own exe, not.
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add(role);
        foreach (var a in args)
            info.ArgumentList.Add(a);
        var log = new StreamWriter(Path.Combine(dir, role + ".log")) { AutoFlush = true };
        try
        {
            var process = Process.Start(info);
            if (process is null)
            {
                log.Dispose();
                return null;
            }
            var child = new Child(process, log, role);
            process.OutputDataReceived += (_, e) => child.Log(e.Data);
            process.ErrorDataReceived += (_, e) => child.Log(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return child;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            log.WriteLine("did not start: " + e.Message);
            log.Dispose();
            return null;
        }
    }

    /// <summary>Waits for the process to end within <paramref name="bound"/>; past it, the process is stopped and the leg is broken.
    /// Its exit code as words, or "" when it had to be stopped.</summary>
    public async Task<string> EndAsync(TimeSpan bound, List<string> problems)
    {
        using var cancel = new CancellationTokenSource(bound > TimeSpan.Zero ? bound : TimeSpan.FromMilliseconds(1));
        try
        {
            await _process.WaitForExitAsync(cancel.Token);
            await _process.WaitForExitAsync(CancellationToken.None);
            return _process.ExitCode.ToString(CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException)
        {
            problems.Add($"the {_role} ran past the leg's deadline and was stopped");
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended between the deadline and the stop: nothing is left to stop.
            }
            return "";
        }
    }

    private void Log(string? line)
    {
        if (line is null)
            return;
        lock (_gate)
        {
            // A line that arrives after the leg let the process go is dropped, never written to a closed log.
            if (!_closed)
                _log.WriteLine(line);
        }
    }

    /// <summary>Stops the process's tree when it is still running (a leg let go early, or the controller failing), so no child
    /// outlives its leg and burns into the next; then lets the process and its log go.</summary>
    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It ended between the look and the stop, or cannot be read: either way it is not left running by this object.
            Log("stop at dispose: " + e.Message);
        }
        _process.Dispose();
        lock (_gate)
        {
            _closed = true;
            _log.Dispose();
        }
    }
}
