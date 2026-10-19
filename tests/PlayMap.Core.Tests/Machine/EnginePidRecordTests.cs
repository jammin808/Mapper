using PlayMap.Core.Machine;

namespace PlayMap.Core.Tests.Machine;

/// <summary>The engine's pid record: it reads back as written; a text that is not one reads null; it is written only into a folder that
/// exists (never making one), never over a record that names another engine still running, and removed only while it still names its
/// own process.</summary>
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

    [Fact]
    public void ARecordThatNamesAnotherRunningProcessIsNeverReplacedAndOneWhoseProcessIsGoneIs()
    {
        var root = Directory.CreateTempSubdirectory("playmap-pid-");
        try
        {
            var path = Path.Combine(root.FullName, EnginePidRecord.FileName);
            // The running engine: this test's own process, alive for as long as the test runs.
            var running = new EnginePidRecord(Environment.ProcessId, 7900, 1_760_000_000_000);
            Assert.True(running.TryWrite(root.FullName));
            var second = new EnginePidRecord(Environment.ProcessId == 4120 ? 4121 : 4120, 7901, 1_760_000_000_500);

            Assert.False(second.TryWrite(root.FullName));
            Assert.Equal(running, EnginePidRecord.Parse(File.ReadAllText(path)));
            Assert.Equal(Environment.ProcessId, EnginePidRecord.Named(root.FullName)?.Pid);

            // The same engine writes its own record again; a record whose process is gone (no pid that high runs) is replaced.
            Assert.True((running with { WirePort = 7902 }).TryWrite(root.FullName));
            File.WriteAllText(path, new EnginePidRecord(int.MaxValue, 7900, 1).ToJson());
            Assert.True(second.TryWrite(root.FullName));
            Assert.Equal(second, EnginePidRecord.Parse(File.ReadAllText(path)));
            // A file that is not a record names no engine and is replaced.
            File.WriteAllText(path, "not a record");
            Assert.True(running.TryWrite(root.FullName));
            Assert.Equal(running, EnginePidRecord.Parse(File.ReadAllText(path)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
