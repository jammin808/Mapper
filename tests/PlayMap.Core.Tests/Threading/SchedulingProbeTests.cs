using System.Diagnostics;
using System.Globalization;
using PlayMap.Tests;

namespace PlayMap.Core.Tests.Threading;

/// <summary>The scheduling probe CI turns on in every test host (tests/Shared/SchedulingProbe.cs): off, it starts nothing and writes
/// nothing; a word other than 1 is refused; on, it writes a header and rows whose times rise and whose columns parse, into the
/// TestResults folder of the project that built the host.</summary>
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
            Assert.Equal(SchedulingProbe.Header, lines[0]);
            Assert.True(lines.Length >= 7, $"{lines.Length} lines");
            var last = DateTime.MinValue;
            foreach (var line in lines.Skip(1))
            {
                var cells = line.Split(',');
                Assert.Equal(6, cells.Length);
                var utc = DateTime.ParseExact(cells[0], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                Assert.Equal(DateTimeKind.Utc, utc.Kind);
                Assert.True(utc > last, $"{cells[0]} follows {last:O}");
                last = utc;
                var overshoot = double.Parse(cells[1], NumberStyles.Float, CultureInfo.InvariantCulture);
                Assert.True(double.IsFinite(overshoot) && overshoot > -SchedulingProbe.Wait.TotalMilliseconds, line);
                var dispatch = double.Parse(cells[2], NumberStyles.Float, CultureInfo.InvariantCulture);
                Assert.True(double.IsFinite(dispatch) && dispatch >= 0, line);
                Assert.Contains(cells[3], new[] { "0", "1" });
                Assert.True(int.Parse(cells[4], NumberStyles.None, CultureInfo.InvariantCulture) >= 0, line);
                Assert.True(long.Parse(cells[5], NumberStyles.None, CultureInfo.InvariantCulture) >= 0, line);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
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
