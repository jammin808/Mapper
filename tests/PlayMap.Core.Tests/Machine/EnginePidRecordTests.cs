using PlayMap.Core.Machine;

namespace PlayMap.Core.Tests.Machine;

/// <summary>The engine's pid record: it reads back as written; a text that is not one reads null, and a file longer than a record is
/// refused by its length before any of it is read; it is written only into a folder that exists (never making one), never over a
/// record that names another PlayMap engine still running by the identity rule, and removed only while it still names its own
/// process.</summary>
public sealed class EnginePidRecordTests
{
    [Fact]
    public void ARecordReadsBackAsWritten()
    {
        var record = new EnginePidRecord(4120, 7900, 1_760_000_000_000);

        Assert.Equal("{\"pid\":4120,\"port\":7900,\"started\":1760000000000}", record.ToJson());
        Assert.Equal(record, EnginePidRecord.Parse(record.ToJson()));
        Assert.Equal(new EnginePidRecord(4120, 0, 0), EnginePidRecord.Parse("{\"pid\":4120,\"port\":0,\"started\":0}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[1]")]
    [InlineData("{\"pid\":0,\"port\":7900,\"started\":1}")]
    [InlineData("{\"pid\":-3,\"port\":7900,\"started\":1}")]
    [InlineData("{\"pid\":4120,\"port\":70000,\"started\":1}")]
    [InlineData("{\"pid\":4120,\"port\":-1,\"started\":1}")]
    [InlineData("{\"pid\":4120,\"port\":7900,\"started\":-1}")]
    [InlineData("{\"pid\":4120,\"port\":7900}")]
    [InlineData("{\"pid\":\"4120\",\"port\":7900,\"started\":1}")]
    [InlineData("{\"pid\":4120.5,\"port\":7900,\"started\":1}")]
    [InlineData("{\"pid\":99999999999,\"port\":7900,\"started\":1}")]
    [InlineData("{\"pid\":")]
    public void ATextThatIsNotARecordReadsNull(string? text) => Assert.Null(EnginePidRecord.Parse(text));

    [Fact]
    public void TheEnginesFolderIsTheOneFolderInsideTheSupervisorsAndHoldsTheRecordApartFromTheStatus()
    {
        // The supervisor's folder holds its status and log (SYSTEM's to write); the record lies in the engine's folder inside it.
        var supervisor = SupervisorStatus.MachineFolder();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PlayMap", "supervisor"), supervisor);
        Assert.Equal(Path.Combine(supervisor, "engine"), EnginePidRecord.MachineFolder());
        Assert.NotEqual(EnginePidRecord.FileName, SupervisorStatus.FileName);
    }

    [Fact]
    public void ATooLongTextReadsNull() =>
        Assert.Null(EnginePidRecord.Parse("{\"pid\":4120,\"port\":7900,\"started\":1,\"x\":\"" + new string('x', EnginePidRecord.MaxBytes) + "\"}"));

    [Fact]
    public void ItIsWrittenOnlyWhereTheFolderExistsAndRemovedOnlyWhileItNamesItsProcess()
    {
        var root = Directory.CreateTempSubdirectory("playmap-pid-");
        try
        {
            var missing = Path.Combine(root.FullName, "supervisor");
            var record = new EnginePidRecord(4120, 7900, 1_760_000_000_000);

            Assert.False(record.TryWrite(missing));
            Assert.False(Directory.Exists(missing));

            Directory.CreateDirectory(missing);
            Assert.True(record.TryWrite(missing));
            var path = Path.Combine(missing, EnginePidRecord.FileName);
            Assert.Equal(record, EnginePidRecord.Parse(File.ReadAllText(path)));
            Assert.Equal(new[] { path }, Directory.GetFiles(missing));

            // Another engine's record (a later one, or another process) is never taken away.
            Assert.False((record with { Pid = 4121 }).TryRemove(missing));
            Assert.False((record with { StartedUnixMs = 1 }).TryRemove(missing));
            Assert.True(File.Exists(path));
            Assert.True(record.TryRemove(missing));
            Assert.False(File.Exists(path));
            Assert.False(record.TryRemove(missing));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private const long Stamped = 1_760_000_000_000;

    /// <summary>A running engine's facts as the identity rule reads them: PlayMap.exe run as the engine, started a second before its
    /// record was stamped.</summary>
    private static ProcessFacts Engine(string name = "PlayMap", long started = Stamped - 1_000, string line = "\"C:\\Program Files\\PlayMap\\PlayMap.exe\" engine --supervised") =>
        new(true, name, "", started, line, "", -1, 0);

    [Fact]
    public void ARecordThatNamesAnotherRunningEngineIsNeverReplaced()
    {
        var root = Directory.CreateTempSubdirectory("playmap-pid-");
        try
        {
            var path = Path.Combine(root.FullName, EnginePidRecord.FileName);
            var running = new EnginePidRecord(4120, 7900, Stamped);
            Assert.True(running.TryWrite(root.FullName, _ => ProcessFacts.Gone, out _));
            var second = new EnginePidRecord(4121, 7901, Stamped + 500);
            var asked = new List<int>();

            Assert.False(second.TryWrite(root.FullName, pid =>
            {
                asked.Add(pid);
                return Engine();
            }, out var holder));
            Assert.Equal([4120], asked);
            Assert.Equal(running, holder);
            Assert.Equal(running, EnginePidRecord.Parse(File.ReadAllText(path)));

            // The same engine writes its own record again, whatever its process reads as.
            Assert.True((running with { WirePort = 7902 }).TryWrite(root.FullName, _ => Engine(), out holder));
            Assert.Null(holder);
            Assert.Equal(7902, EnginePidRecord.Named(root.FullName)?.WirePort);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ARecordThatNamesNoRunningEngineIsReplaced()
    {
        (string Why, ProcessFacts Facts)[] noEngine =
        [
            ("its process is gone", ProcessFacts.Gone),
            ("its pid is another image's now", Engine(name: "notepad", line: "notepad.exe")),
            ("its pid was taken after the record was written", Engine(started: Stamped + 60_000)),
            ("its pid was another process's long before the record", Engine(started: Stamped - 600_000)),
            ("its process is PlayMap run as the desk", Engine(line: "\"C:\\Program Files\\PlayMap\\PlayMap.exe\"")),
            ("its process cannot be read", new ProcessFacts(true, "", "", -1, "", "", -1, 0)),
        ];
        foreach (var (why, facts) in noEngine)
        {
            var root = Directory.CreateTempSubdirectory("playmap-pid-");
            try
            {
                File.WriteAllText(Path.Combine(root.FullName, EnginePidRecord.FileName), new EnginePidRecord(4120, 7900, Stamped).ToJson());
                var next = new EnginePidRecord(4121, 7901, Stamped + 500);
                Assert.True(next.TryWrite(root.FullName, _ => facts, out var holder), why);
                Assert.Null(holder);
                Assert.Equal(next, EnginePidRecord.Named(root.FullName));
            }
            finally
            {
                root.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public void AFileThatIsNotARecordIsReplaced()
    {
        var root = Directory.CreateTempSubdirectory("playmap-pid-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, EnginePidRecord.FileName), "not a record");
            var record = new EnginePidRecord(4121, 7901, Stamped);
            Assert.True(record.TryWrite(root.FullName, _ => Engine(), out var holder));
            Assert.Null(holder);
            Assert.Equal(record, EnginePidRecord.Named(root.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void AFileLongerThanARecordIsRefusedByItsLengthBeforeAnyOfItIsRead()
    {
        // The engine's folder is the show user's to write: a file there grown to a gigabyte (its length set, nothing written, as the
        // show user may grow one at almost no cost on the disk) is refused by its length, its reader allocating no more than a record
        // costs, and the engine's own write replaces it; a record padded past the limit is refused too, and one at the limit is read.
        var root = Directory.CreateTempSubdirectory("playmap-pid-");
        try
        {
            var path = Path.Combine(root.FullName, EnginePidRecord.FileName);
            var record = new EnginePidRecord(4120, 7900, Stamped);
            File.WriteAllText(path, record.ToJson() + new string(' ', EnginePidRecord.MaxBytes - record.ToJson().Length));
            Assert.Equal(record, EnginePidRecord.Named(root.FullName));
            File.WriteAllText(path, record.ToJson() + new string(' ', EnginePidRecord.MaxBytes - record.ToJson().Length + 1));
            Assert.Null(EnginePidRecord.Named(root.FullName));

            using (var grown = new FileStream(path, FileMode.Create, FileAccess.Write))
                grown.SetLength(1L << 30);
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Null(EnginePidRecord.Named(root.FullName));
            Assert.False(record.TryRemove(root.FullName));
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 1 << 20, $"reading a grown file allocated {allocated} bytes");
            Assert.Equal(1L << 30, new FileInfo(path).Length);

            Assert.True(record.TryWrite(root.FullName, _ => Engine(), out var holder));
            Assert.Null(holder);
            Assert.Equal(record, EnginePidRecord.Named(root.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ARecordNamingALiveProcessThatIsNoEngineIsReplacedByWhatThisOsSays()
    {
        // This test's own process runs as long as the test does and is not PlayMap's engine: a stale record whose pid it holds keeps
        // no engine from writing its own.
        var root = Directory.CreateTempSubdirectory("playmap-pid-");
        try
        {
            var stale = new EnginePidRecord(Environment.ProcessId, 7900, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            File.WriteAllText(Path.Combine(root.FullName, EnginePidRecord.FileName), stale.ToJson());
            Assert.True(ProcessReader.Read(Environment.ProcessId).Exists);
            var engine = new EnginePidRecord(Environment.ProcessId == 4120 ? 4121 : 4120, 7901, stale.StartedUnixMs + 500);
            Assert.True(engine.TryWrite(root.FullName, out var holder));
            Assert.Null(holder);
            Assert.Equal(engine, EnginePidRecord.Named(root.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
