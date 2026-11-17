using System.Diagnostics;
using System.Globalization;
using PlayMap.Tests;

namespace PlayMap.Core.Tests.Threading;

/// <summary>The scheduling probe CI turns on in every test host (tests/Shared/SchedulingProbe.cs): off, it starts nothing and writes
/// nothing; a word other than 1 is refused; on, it writes the machine's facts, the columns and rows whose times rise and whose
/// eighteen columns parse, into the TestResults folder of the project that built the host.</summary>
public sealed class SchedulingProbeTests
{
    [Fact]
    public void WithoutItsVariableTheProbeStartsNothingAndWritesNothing()
    {
        var root = Directory.CreateTempSubdirectory("playmap-probe-");
        try
        {
            var folder = Path.Combine(root.FullName, "results");
            var asked = 0;
            Assert.Null(SchedulingProbe.Start(null, () => { asked++; return folder; }, "off"));
            Assert.Null(SchedulingProbe.Start("", () => { asked++; return folder; }, "off"));
            Assert.Equal(0, asked);
            Assert.False(Directory.Exists(folder));

            // Any other word is refused, never read as on or off.
            foreach (var word in new[] { "0", "yes", "true", " 1", "1 " })
                Assert.Throws<ArgumentException>(() => SchedulingProbe.Start(word, () => folder, "refused"));
            Assert.False(Directory.Exists(folder));

            // This host's own probe follows the same variable: off here unless the run turned it on.
            Assert.Equal(Environment.GetEnvironmentVariable(SchedulingProbe.Variable) == "1", SchedulingProbe.Running is not null);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void OnItWritesRowsWhoseTimesRiseAndWhoseColumnsParse()
    {
        var root = Directory.CreateTempSubdirectory("playmap-probe-");
        try
        {
            var folder = Path.Combine(root.FullName, "TestResults");
            string path;
            using (var probe = SchedulingProbe.Start("1", () => folder, "PlayMap.Probe.Tests")!)
            {
                path = probe.Path;
                Assert.Equal(folder, Path.GetDirectoryName(path));
                Assert.StartsWith("PlayMap.Probe.Tests-", Path.GetFileName(path), StringComparison.Ordinal);
                Assert.EndsWith($"-{Environment.ProcessId}.latency.csv", path, StringComparison.Ordinal);

                // Six rows at 50 ms each: within 20 s even on a loaded runner.
                using var gate = new ManualResetEventSlim(false);
                var waited = Stopwatch.StartNew();
                while (probe.Rows < 6 && waited.Elapsed < TimeSpan.FromSeconds(20))
                    gate.Wait(20);
                Assert.True(probe.Rows >= 6, $"the probe wrote {probe.Rows} rows in {waited.Elapsed.TotalSeconds:0.0} s");
            }

            var lines = File.ReadAllLines(path);
            // The first line is the machine's facts, the second the columns; the first six columns are the first probe's, in its order.
            var facts = Facts(lines[0]);
            Assert.Equal(Environment.ProcessorCount, Whole(facts["processors"]));
            ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
            ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
            Assert.Equal(minWorkers, Whole(facts["pool_min_workers"]));
            Assert.Equal(minIo, Whole(facts["pool_min_io"]));
            Assert.Equal(maxWorkers, Whole(facts["pool_max_workers"]));
            Assert.Equal(maxIo, Whole(facts["pool_max_io"]));
            Assert.Equal(Environment.ProcessId, Whole(facts["pid"]));
            Assert.Equal(SchedulingProbe.Header, lines[1]);
            Assert.StartsWith("utc,wake_overshoot_ms,dispatch_ms,dispatch_pending,pool_threads,pool_pending,", SchedulingProbe.Header,
                StringComparison.Ordinal);
            var columns = SchedulingProbe.Header.Split(',');
            Assert.Equal(18, columns.Length);
            Assert.True(lines.Length >= 8, $"{lines.Length} lines");

            var last = DateTime.MinValue;
            var gen2 = 0L;
            var beats = 0;
            foreach (var line in lines.Skip(2))
            {
                var cells = line.Split(',');
                Assert.Equal(columns.Length, cells.Length);
                var row = columns.Zip(cells).ToDictionary(c => c.First, c => c.Second, StringComparer.Ordinal);
                var utc = DateTime.ParseExact(row["utc"], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                Assert.Equal(DateTimeKind.Utc, utc.Kind);
                Assert.True(utc > last, $"{row["utc"]} follows {last:O}");
                last = utc;
                // Each wait's overshoot is past a wait asked in whole milliseconds; a timer may end it a little early, never by a wait.
                Assert.True(Real(row["wake_overshoot_ms"]) > -SchedulingProbe.Wait.TotalMilliseconds, line);
                Assert.True(Real(row["rest_overshoot_ms"]) > -SchedulingProbe.Wait.TotalMilliseconds, line);
                Assert.True(Real(row["dispatch_ms"]) >= 0, line);
                Assert.Contains(row["dispatch_pending"], new[] { "0", "1" });
                Assert.True(Whole(row["pool_threads"]) >= 0, line);
                Assert.True(Whole(row["pool_pending"]) >= 0, line);
                // Counts since the row before, and times since it, are never below 0; the gen2 count never falls.
                Assert.True(Whole(row["pool_completed"]) >= 0, line);
                Assert.True(Real(row["gc_pause_ms"]) >= 0, line);
                var collections = Whole(row["gc_gen2"]);
                Assert.True(collections >= gen2, line);
                gen2 = collections;
                Assert.True(Real(row["working_set_mb"]) > 0, line);
                Assert.True(Real(row["cpu_user_ms"]) >= 0 && Real(row["cpu_kernel_ms"]) >= 0, line);
                Assert.True(Real(row["last_write_ms"]) >= 0 && Real(row["pool_read_ms"]) >= 0, line);
                Assert.True(Real(row["beat_age_ms"]) >= 0 && Real(row["beat_gap_ms"]) >= 0, line);
                Assert.True(Real(row["process_read_ms"]) >= 0, line);
                if (Real(row["beat_gap_ms"]) > 0)
                    beats++;
            }
            // The beat, at the highest priority, stamped between rows (a pool or the processors the tests beside hold can keep the
            // other counts at 0 for a few rows).
            Assert.True(beats > 0, "the beat stamped in no row");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static Dictionary<string, string> Facts(string line)
    {
        Assert.StartsWith(SchedulingProbe.FactsMark + " ", line, StringComparison.Ordinal);
        return line[(SchedulingProbe.FactsMark.Length + 1)..].Split(' ')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
    }

    private static long Whole(string cell) => long.Parse(cell, NumberStyles.None, CultureInfo.InvariantCulture);

    private static double Real(string cell)
    {
        var value = double.Parse(cell, NumberStyles.Float, CultureInfo.InvariantCulture);
        Assert.True(double.IsFinite(value), cell);
        return value;
    }

    [Fact]
    public void AHostsRowsGoToTheTestResultsOfTheProjectThatBuiltIt()
    {
        var assembly = typeof(SchedulingProbeTests).Assembly.GetName().Name!;
        var folder = SchedulingProbe.ResultsFolder(AppContext.BaseDirectory, assembly);
        Assert.Equal("TestResults", Path.GetFileName(folder));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(folder)!, assembly + ".csproj")));

        // A host with no project above it is refused: its rows would go nowhere a run uploads.
        var root = Directory.CreateTempSubdirectory("playmap-probe-");
        try
        {
            Assert.Throws<InvalidOperationException>(() => SchedulingProbe.ResultsFolder(root.FullName, assembly));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
