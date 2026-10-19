using PlayMap.Core.Effects;

namespace PlayMap.Core.Tests.Shaders;

/// <summary>The quarantine record: kept in the node's home and read again at start, entries in order with every field; an add of a
/// kind already held and a release of one not held change nothing (and write nothing); ReleaseAll answers what it lifted; a record that
/// cannot be read quarantines nothing and says why; every change is raised once.</summary>
public sealed class ShaderQuarantineTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("playmap-quarantine-").FullName;

    private string Path_ => Path.Combine(_folder, "playmap.quarantine.json");

    private static QuarantineEntry Entry(string hash, QuarantineReason reason = QuarantineReason.GpuTime) =>
        new(hash, "Kind " + hash, reason, "words of " + hash, 1_760_000_000_000, "0.2.0+17.abc1234");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void TheRecordIsKeptInTheHomeAndReadAgainAtStart()
    {
        var q = new ShaderQuarantine(Path_);
        var changes = 0;
        q.Changed += () => changes++;
        Assert.True(q.Add(Entry("a")));
        Assert.True(q.Add(Entry("b", QuarantineReason.DeviceRemoved)));
        Assert.True(q.Add(Entry("c", QuarantineReason.CompilerStopped)));
        Assert.Equal(3, changes);

        var again = new ShaderQuarantine(Path_);
        Assert.Equal(q.All.ToArray(), again.All.ToArray());
        Assert.Equal("", again.LoadWords);
        Assert.True(again.IsQuarantined("b", out var b));
        Assert.Equal(Entry("b", QuarantineReason.DeviceRemoved), b);
        Assert.Equal(["a", "b", "c"], again.Hashes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AChangeThatChangesNothingIsRefusedAndWritesNothing()
    {
        var q = new ShaderQuarantine(Path_);
        var changes = 0;
        q.Changed += () => changes++;
        Assert.False(q.Release("a"));
        Assert.Empty(q.ReleaseAll());
        Assert.False(File.Exists(Path_));
        q.Add(Entry("a"));
        var written = File.ReadAllText(Path_);
        Assert.False(q.Add(Entry("a", QuarantineReason.DeviceRemoved)));
        Assert.Equal(QuarantineReason.GpuTime, q.All[0].Reason);
        Assert.Equal(written, File.ReadAllText(Path_));
        Assert.Equal(1, changes);

        Assert.True(q.Release("a"));
        Assert.False(q.IsQuarantined("a", out _));
        Assert.Empty(new ShaderQuarantine(Path_).All);
        q.Add(Entry("x"));
        q.Add(Entry("y"));
        Assert.Equal(["x", "y"], q.ReleaseAll().Select(e => e.KindHash));
        Assert.Empty(q.All);
        Assert.Empty(q.Hashes);
    }

    [Fact]
    public void ARecordThatCannotBeReadQuarantinesNothingAndSaysWhy()
    {
        File.WriteAllText(Path_, "{ not json");
        var q = new ShaderQuarantine(Path_);
        Assert.Empty(q.All);
        Assert.Contains("could not be read", q.LoadWords, StringComparison.Ordinal);
        File.WriteAllText(Path_, "{\"quarantine\":[{\"hash\":\"a\",\"kind\":\"k\",\"reason\":\"melted\",\"words\":\"\",\"at\":0,\"build\":\"\"}]}");
        Assert.Contains("is not a quarantine reason", new ShaderQuarantine(Path_).LoadWords, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInMemoryQuarantineWritesNoFile()
    {
        var q = new ShaderQuarantine();
        Assert.True(q.Add(Entry("a")));
        Assert.Single(q.All);
        Assert.Empty(Directory.EnumerateFiles(_folder));
    }
}
