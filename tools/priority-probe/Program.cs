using System.Diagnostics;
using System.Globalization;
using PlayMap.Core.Time;

namespace PlayMap.PriorityProbe;

/// <summary>What a saturating process costs a Normal-class process doing real work, at the burner's HIGH and at its NORMAL, and what a
/// deadline loop claiming HIGH or NORMAL keeps beside a competing NORMAL load, in legs. A measurement for the owner to read, never a pass
/// mark: a run fails only when a leg wrote no files, a process did not start or did not read back the class it was told, or a leg ran
/// past its deadline.
///   calibrate --out ROOT                   the frame's fixed work, about 10 ms of wall time on the quiet machine, to ROOT/calibration.csv,
///                                          and the runner's scheduler as read then to ROOT/scheduler.csv
///   order                                  the legs' order (<see cref="Legs.Order"/>), one line each: its place, its leg and its folder
///   leg --out ROOT --run K [--seconds S] [--lead L] [--node EXE --clips DIR]   the order's K-th leg into ROOT/leg-KK-NAME (a node
///                                          leg needs the node, its exe or dll, and the folder of HAP clips it plays: <see cref="NodeRun"/>)
///   read --out ROOT                        the tables of every leg present, to ROOT/summary.md and ROOT/summary.csv, and printed
///   legs --out ROOT [--seconds S] [--lead L] [--node EXE --clips DIR]   calibrate, every leg of the order, then read (a local run; CI
///                                          runs the steps one by one)
///   selfcheck --fixture DIR                the reader over a copy of DIR's hand-made legs, each figure DIR/expected.csv names held to
///                                          its value and each line DIR/expected-summary.tsv names looked for in summary.md (exit 1
///                                          with the words of each one that is not)
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
            Say("say a mode: calibrate, order, leg, read, legs or selfcheck (docs: README, Build; docs/round2/CARRYOVER.md, fix-ci-priority-measure and fix-priority-probe-reader)");
            return 2;
        }
        try
        {
            var args = new Args(argv.Skip(1));
            return argv[0] switch
            {
                "calibrate" => Calibration.Run(args.Text("out")),
                "order" => Legs.SayOrder(),
                "leg" => await Legs.RunAsync(args.Text("out"), Legs.AtPlace(args.Integer("run")), Seconds(args), Lead(args), NodeGiven.Of(args)),
                "read" => Reader.Run(args.Text("out")),
                "legs" => await Legs.AllAsync(args.Text("out"), Seconds(args), Lead(args), NodeGiven.Of(args)),
                "selfcheck" => SelfCheck.Run(args.Text("fixture")),
                "burner" => Burner.Run(args),
                "victim" => Victim.Run(args),
                _ => throw new ArgumentException($"'{argv[0]}' is not a mode: calibrate, order, leg, read, legs or selfcheck"),
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
        Program.Say($"calibration: {iterations} iterations, median {Fmt(samples[15])} ms (p10 {Fmt(samples[3])}, p90 {Fmt(samples[27])}) on {Environment.ProcessorCount} logical processors");
        Scheduler.Write(Path.Combine(root, Scheduler.File), "the job's reading before any leg");
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

/// <summary>One leg kind: what burns beside the victim (nothing in the baseline), how many threads, the class the burner claims, whether
/// its threads are pinned (core1: every core but one; n1: the logical processors less one, one thread to each), the victim's class
/// (Normal set by its own hand, kept as the runner started it, or claimed HIGH or NORMAL through the product's platform: then the
/// victim's loops are the busy side's own deadline), and, for a node leg, the process class PlayMap's own node is brought to (high,
/// its default, or normal, by its own MACHINE SET PROCESS; <see cref="NodeRun"/>), beside the victim alone or beside the victim and a
/// burner.</summary>
internal sealed record LegSpec(string Name, string Label, int Threads, string BurnerClass, string VictimClass, string Pin = "none", string Node = "")
{
    /// <summary>The victim's loops stand for the busy side's own deadline (the buy legs), not for a desk beside it.</summary>
    public bool Deadline => VictimClass.StartsWith("claim-", StringComparison.Ordinal);

    /// <summary>The busy process is the product's own node (at the process class <see cref="Node"/>), not only the burner.</summary>
    public bool IsNode => Node.Length > 0;
}

/// <summary>A place in the order: the leg kind it runs, "warmup" for the leg run first and never counted with its kind, and why it is
/// there (said in the summary beside the place).</summary>
internal sealed record Placed(string Kind, string Label = "", string Note = "")
{
    public bool Warmup => Label == "warmup";
}

internal static class Legs
{
    /// <summary>The logical processors less one, at least one (<see cref="Environment.ProcessorCount"/> counts logical processors, so on
    /// a machine with SMT this leaves one logical processor free, the SMT sibling of a busy one, never a whole core): the width each of
    /// the product's Compute pool (src/PlayMap.Core/Threading/Compute.cs 102) and its HAP workers (src/PlayMap.Media/Hap/HapWorkers.cs 92)
    /// takes on its own. Nothing in the product caps their sum (with decode's and the render thread's), so a leg on the logical processors
    /// less one stands for a node whose whole demand stays there, which the code does not hold it to.</summary>
    public static int LogicalLessOne => Math.Max(1, Environment.ProcessorCount - 1);

    private static readonly Lazy<CorePlan> CorePlanOnce = new(Os.WholeCoreFree);

    /// <summary>The whole-core-free legs' plan on this machine, read once (<see cref="Os.WholeCoreFree"/>).</summary>
    public static CorePlan CorePlan => CorePlanOnce.Value;

    private static readonly Lazy<CorePlan> LessOnePlanOnce = new(Os.LessOnePinned);

    /// <summary>The pinned one-free leg's plan on this machine, read once (<see cref="Os.LessOnePinned"/>).</summary>
    public static CorePlan LessOnePlan => LessOnePlanOnce.Value;

    /// <summary>The legs one job runs, in this order, the one place it is written. A baseline at each end, the first reading the quiet
    /// machine. The warm-up is the first burned leg, at place 2, after that first baseline: a high-n1, never counted with its kind, so
    /// whatever the job's first burned leg meets is met by it and not by a counted one. Then high-n1 at the first counted burned place,
    /// again in the middle and again at the last burned place, each beside a normal-n1, so a freeze that follows high-n1 to its later
    /// places points to the class with one logical processor free and one that stays at the job's start does not. Between them the
    /// other kinds, each twice and alternated (A B B A): what HIGH buys (buy-high, buy-normal), a whole core free (high-core1,
    /// normal-core1) with the pinned one-free leg beside it (high-n1-pinned: high-n1's threads and its one free logical processor,
    /// pinned, so beside high-n1 it differs in the pinning alone, and beside high-core1 in the free core with the one thread fewer
    /// that freeing a core of two logical processors takes), and the real node's four (node-high, node-normal, node-high-load, node-normal-load: A B C D,
    /// then D C B A). The every-processor pairs after the middle (A B B A A B, three of each: their stalls run longest), and the runner's
    /// leg once. Each leg's folder is named by its place, so a repeat never meets the files of the one before.</summary>
    public static readonly IReadOnlyList<Placed> Order =
    [
        new("baseline", Note: "the first baseline (after the quiet start)"),
        new("high-n1", "warmup", "the warm-up: the first burned leg (never counted with its kind)"),
        new("high-n1", Note: "high-n1 at the first counted burned place"),
        new("normal-n1"),
        new("buy-high"),
        new("buy-normal"),
        new("high-core1"),
        new("high-n1-pinned"),
        new("normal-core1"),
        new("node-high"),
        new("node-normal"),
        new("node-high-load"),
        new("node-normal-load"),
        new("normal-core1"),
        new("high-n1-pinned"),
        new("high-core1"),
        new("buy-normal"),
        new("normal-n1"),
        new("high-n1", Note: "high-n1 in the middle"),
        new("buy-high"),
        new("node-normal-load"),
        new("node-high-load"),
        new("node-normal"),
        new("node-high"),
        new("normal-n"),
        new("high-n"),
        new("high-n"),
        new("normal-n"),
        new("normal-n"),
        new("high-n"),
        new("high-n-runner"),
        new("normal-n1"),
        new("high-n1", Note: "high-n1 at the last burned place"),
        new("baseline", Note: "the last baseline"),
    ];

    /// <summary>The order's leg at place <paramref name="place"/> (from 1), refused with words outside it.</summary>
    public static (int Place, Placed At, LegSpec Spec) AtPlace(long place) =>
        place >= 1 && place <= Order.Count
            ? ((int)place, Order[(int)place - 1], Find(Order[(int)place - 1].Kind))
            : throw new ArgumentException($"--run takes a place in the order, 1 to {Order.Count}, not {place}");

    /// <summary>A leg's folder under the root: its place in two digits, its kind and, for the warm-up, its label.</summary>
    public static string Folder(int place, Placed at) =>
        $"leg-{place.ToString("00", CultureInfo.InvariantCulture)}-{at.Kind}{(at.Label.Length > 0 ? "-" + at.Label : "")}";

    public static int SayOrder()
    {
        for (var i = 0; i < Order.Count; i++)
            Program.Say($"{(i + 1).ToString(CultureInfo.InvariantCulture)} {Order[i].Kind}{(Order[i].Label.Length > 0 ? ":" + Order[i].Label : "")} {Folder(i + 1, Order[i])}");
        return 0;
    }

    public static IReadOnlyList<LegSpec> All
    {
        get
        {
            var n = Environment.ProcessorCount;
            var lessOne = LogicalLessOne;
            var plan = CorePlan;
            var core = plan.Made
                ? $"pinned one to each logical processor of every core but one ({plan.Threads} threads on {Os.Join(plan.Pinned)}; {plan.Words})"
                : $"not pinned: {plan.Words}";
            var one = LessOnePlan;
            var pinnedOne = one.Made
                ? $"pinned one to each of the logical processors less one ({one.Threads} threads on {Os.Join(one.Pinned)}; {one.Words})"
                : $"not pinned: {one.Words}";
            const string nodeWork = "PlayMap's own node playing the job's HAP clips in a loop, one 64 x 36 output enabled per clip (a node decodes only what an enabled output draws)";
            return
            [
                new("baseline", "no burner; the victim at Normal", 0, "", "normal"),
                new("high-n", $"the burner HIGH on every logical processor ({n} threads); the victim at Normal", n, "high", "normal"),
                new("normal-n", $"the burner NORMAL on every logical processor ({n} threads); the victim at Normal", n, "normal", "normal"),
                new("high-n1", $"the burner HIGH on the logical processors less one ({lessOne} threads, unpinned); the victim at Normal", lessOne, "high", "normal"),
                new("normal-n1", $"the burner NORMAL on the logical processors less one ({lessOne} threads, unpinned); the victim at Normal", lessOne, "normal", "normal"),
                new("high-n-runner", $"the burner HIGH on every logical processor ({n} threads); the victim at the runner's own class (the runner's condition, not a desk's)", n, "high", "keep"),
                new("buy-high", $"the busy side's own deadline: the victim's loops in a process claiming HIGH through the product's platform beside a burner NORMAL on every logical processor ({n} threads)", n, "normal", "claim-high"),
                new("buy-normal", $"the busy side's own deadline: the victim's loops in a process claiming NORMAL through the product's platform beside a burner NORMAL on every logical processor ({n} threads)", n, "normal", "claim-normal"),
                new("high-core1", $"the burner HIGH {core}; the victim at Normal and unpinned", plan.Threads, "high", "normal", "core1"),
                new("normal-core1", $"the burner NORMAL {core}; the victim at Normal and unpinned", plan.Threads, "normal", "normal", "core1"),
                new("high-n1-pinned", $"the burner HIGH {pinnedOne}; the victim at Normal and unpinned", one.Threads, "high", "normal", "n1"),
                new("node-high", $"{nodeWork}, its process class high (its default); the victim at Normal", 0, "", "normal", Node: "high"),
                new("node-normal", $"{nodeWork}, its process class normal (MACHINE SET PROCESS NORMAL); the victim at Normal", 0, "", "normal", Node: "normal"),
                new("node-high-load", $"{nodeWork}, its process class high (its default), beside a burner NORMAL on every logical processor ({n} threads); the victim at Normal", n, "normal", "normal", Node: "high"),
                new("node-normal-load", $"{nodeWork}, its process class normal (MACHINE SET PROCESS NORMAL), beside a burner NORMAL on every logical processor ({n} threads); the victim at Normal", n, "normal", "normal", Node: "normal"),
            ];
        }
    }

    public static LegSpec Find(string name) =>
        All.FirstOrDefault(l => l.Name == name) ?? throw new ArgumentException($"'{name}' is not a leg: {string.Join(", ", All.Select(l => l.Name))}");

    public const double ReadyBoundSeconds = 60;
    public const double AfterWindowBoundSeconds = 90;

    public static async Task<int> AllAsync(string root, double seconds, double lead, NodeGiven? node)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException($"{root} is not empty: the legs are written into a folder of their own");
        var broken = Calibration.Run(root) != 0;
        for (var place = 1; place <= Order.Count; place++)
            broken |= await RunAsync(root, AtPlace(place), seconds, lead, node) != 0;
        Reader.Run(root);
        return broken ? 1 : 0;
    }

    /// <summary>Runs one leg: for a node leg the node first, started, given its class and brought to work (<see cref="NodeRun"/>); then
    /// the victim started and ready, then the burner started into the victim's window and told to stop a second after it, both waited
    /// for under the leg's deadline; a node is stopped once the victim has ended. Writes the leg's row; 0, or 1 when the leg is broken
    /// (the words say why).</summary>
    public static async Task<int> RunAsync(string root, (int Place, Placed At, LegSpec Spec) at, double seconds, double lead, NodeGiven? node = null)
    {
        if (seconds <= 0)
            throw new ArgumentException("--seconds takes a number above zero");
        var leg = at.Spec;
        var dir = Path.Combine(root, Folder(at.Place, at.At));
        Directory.CreateDirectory(dir);
        if (new[] { "victim.ready", "burner.ready", "victim.csv", "burner.csv", "leg.csv", NodeRun.File }.Any(f => File.Exists(Path.Combine(dir, f))))
            throw new ArgumentException($"{dir} holds a leg's files already: a leg is written into a folder of its own");
        var started = DateTime.UtcNow;
        var problems = new List<string>();
        var victimExit = "";
        var burnerExit = "";
        var victimReadyUtc = "";
        var burnerReadyUtc = "";
        var name = leg.Name + (at.At.Warmup ? " (the warm-up)" : "");
        Program.Say($"leg {at.Place.ToString(CultureInfo.InvariantCulture)} {name}: {leg.Label}, {seconds.ToString("0.#", CultureInfo.InvariantCulture)} s");

        var work = Calibration.Read(root);
        NodeRun? nodeRun = null;
        if (work is null)
        {
            problems.Add("no calibration: run calibrate first");
        }
        else if (leg.IsNode && node is null)
        {
            problems.Add("a node leg and no node given: run it with --node (the node's exe or dll) and --clips (the folder of its HAP clips)");
        }
        else
        {
            if (leg.IsNode)
            {
                // The node first: started, given its class and brought to work before the victim's window is made, so the window
                // never holds the node's start; the controller stamps it from a process of the class the node cannot hold.
                nodeRun = await NodeRun.StartAsync(dir, node!, leg.Node, problems);
            }
            var deadline = Stopwatch.StartNew();
            var bound = TimeSpan.FromSeconds(ReadyBoundSeconds + lead + seconds + AfterWindowBoundSeconds);
            Child? victim = null;
            Child? burner = null;
            try
            {
                // The victim is started inside the try whose finally stops the node, so a fault at its start never leaves a node at
                // work through the later legs.
                victim = nodeRun is { Working: false } ? null
                    : Child.Start(dir, "victim", ["--dir", dir, "--seconds", Num(seconds), "--lead", Num(lead), "--work", Num(work.Value), "--class", leg.VictimClass]);
                if (nodeRun is { Working: false })
                {
                    // Said by the node's own words; no victim is started beside a node that is not at work.
                }
                else if (victim is null)
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
                        var start = long.Parse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture);
                        var end = long.Parse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture);
                        nodeRun?.StampAt(start, end);
                        if (leg.Threads > 0)
                        {
                            // The burner stops itself a second after the victim's window, on the machine's monotonic clock: nothing waits on
                            // this controller, whose own class is the runner's and which a HIGH burner can hold.
                            burner = Child.Start(dir, "burner", ["--dir", dir, "--threads", Num(leg.Threads), "--class", leg.BurnerClass,
                                "--from", Num(start), "--to", Num(end), "--until", Num(end + 1_000_000_000L), "--pin", leg.Pin]);
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
                victim?.Dispose();
                burner?.Dispose();
                // The node is stopped once the victim's window has ended and its files are written (or the leg let go), then its row
                // and its threads' rows are written: a node never outlives its leg.
                nodeRun?.Stop(problems);
                nodeRun?.Dispose();
            }
            if (victimExit == "3")
                problems.Add($"the victim was told {(leg.Deadline ? "to claim " + leg.VictimClass["claim-".Length..] : "Normal")} and Windows reads back another class");
            else if (victimExit is not "" and not "0")
                problems.Add($"the victim ended with exit {victimExit}");
            if (burnerExit == "3")
                problems.Add("the burner was told its class and Windows reads back another");
            else if (burnerExit is not "" and not "0")
                problems.Add($"the burner ended with exit {burnerExit}");
            var files = new List<string> { "victim.csv", "frames.csv", "ticks.csv", "pool.csv" };
            if (leg.Threads > 0)
                files.Add("burner.csv");
            if (leg.IsNode)
                files.Add(NodeRun.File);
            foreach (var file in files)
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
            victimExit, burnerExit, outcome, string.Join("; ", problems), at.Place.ToString(CultureInfo.InvariantCulture),
            at.At.Label, at.At.Note,
        ]]);
        Program.Say($"leg {at.Place.ToString(CultureInfo.InvariantCulture)} {name}: {outcome}{(problems.Count > 0 ? ": " + string.Join("; ", problems) : "")}");
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

    /// <summary>The process's exit code once it has ended; null while it runs.</summary>
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

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
        return Launch(info, dir, role);
    }

    /// <summary>Another program as a process of the leg (the node): an exe run as itself, a dll run by the dotnet that runs this probe
    /// (or the dotnet on the path when the probe runs as its own exe), its output and errors kept in the leg's folder as role.log.</summary>
    public static Child? StartProgram(string dir, string role, string program, string[] args)
    {
        ProcessStartInfo info;
        if (program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var self = Environment.ProcessPath ?? "";
            var dotnet = string.Equals(Path.GetFileNameWithoutExtension(self), "dotnet", StringComparison.OrdinalIgnoreCase) ? self : "dotnet";
            info = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(program);
        }
        else
        {
            info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        }
        foreach (var a in args)
            info.ArgumentList.Add(a);
        return Launch(info, dir, role);
    }

    public int Id => _process.Id;

    private static Child? Launch(ProcessStartInfo info, string dir, string role)
    {
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

    /// <summary>Stops the process's tree when it is still running and waits, within <paramref name="bound"/>, for the process to have
    /// ended (on Windows a stop returns before the process's handles close, so its files can still be in use); true once it has ended.
    /// Its log stays open until <see cref="Dispose"/>.</summary>
    public bool StopAndWait(TimeSpan bound)
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            return _process.WaitForExit(bound);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It ended between the look and the stop, or cannot be read: said in its log, and read as ended only if it says so.
            Log("stop and wait: " + e.Message);
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
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
