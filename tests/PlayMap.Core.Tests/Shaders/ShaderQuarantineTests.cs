using PlayMap.Core.Effects;

namespace PlayMap.Core.Tests.Shaders;

/// <summary>The quarantine record: kept in the node's home and read again at start, entries in order with every field; an add of a
/// kind already held and a release of one not held change nothing (and write nothing); ReleaseAll answers what it lifted; a record that
/// cannot be read is kept aside before anything can write over it, and says why; an entry whose reason this build does not know stays
/// quarantined with its word; a change the home refuses to record changes nothing, in memory or on disk; every applied change is
/// raised once.</summary>
public sealed class ShaderQuarantineTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("playmap-quarantine-").FullName;

    private string Path_ => Path.Combine(_folder, "playmap.quarantine.json");

    private static QuarantineEntry Entry(string hash, QuarantineReason reason = QuarantineReason.GpuTime) =>
        new(hash, "Kind " + hash, reason, "words of " + hash, 1_760_000_000_000, "0.2.0+17.abc1234");

    private string[] Aside() => Directory.GetFiles(_folder, "playmap.quarantine.json.unreadable-*");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void TheRecordIsKeptInTheHomeAndReadAgainAtStart()
    {
        var q = new ShaderQuarantine(Path_);
        var changes = 0;
        q.Changed += () => changes++;
        Assert.True(q.Add(Entry("a")).Applied);
        Assert.True(q.Add(Entry("b", QuarantineReason.DeviceRemoved)).Applied);
        Assert.True(q.Add(Entry("c", QuarantineReason.CompilerStopped)).Applied);
        Assert.Equal(3, changes);

        var again = new ShaderQuarantine(Path_);
        Assert.Equal(q.All.ToArray(), again.All.ToArray());
        Assert.Equal("", again.LoadWords);
        Assert.True(again.IsQuarantined("b", out var b));
        Assert.Equal(Entry("b", QuarantineReason.DeviceRemoved), b);
        Assert.Equal(["a", "b", "c"], again.Hashes.Order(StringComparer.Ordinal));
        Assert.Empty(Aside());
    }

    [Fact]
    public void AChangeThatChangesNothingIsRefusedAndWritesNothing()
    {
        var q = new ShaderQuarantine(Path_);
        var changes = 0;
        q.Changed += () => changes++;
        Assert.Equal(QuarantineOutcome.Unchanged, q.Release("a").Outcome);
        Assert.Equal(QuarantineOutcome.Unchanged, q.ReleaseAll().Outcome);
        Assert.False(File.Exists(Path_));
        Assert.True(q.Add(Entry("a")).Applied);
        var written = File.ReadAllText(Path_);
        Assert.Equal(QuarantineOutcome.Unchanged, q.Add(Entry("a", QuarantineReason.DeviceRemoved)).Outcome);
        Assert.Equal(QuarantineReason.GpuTime, q.All[0].Reason);
        Assert.Equal(written, File.ReadAllText(Path_));
        Assert.Equal(1, changes);

        var released = q.Release("a");
        Assert.True(released.Applied);
        Assert.Equal([Entry("a")], released.Entries.ToArray());
        Assert.False(q.IsQuarantined("a", out _));
        Assert.Empty(new ShaderQuarantine(Path_).All);
        q.Add(Entry("x"));
        q.Add(Entry("y"));
        Assert.Equal(["x", "y"], q.ReleaseAll().Entries.Select(e => e.KindHash));
        Assert.Empty(q.All);
        Assert.Empty(q.Hashes);
    }

    [Fact]
    public void ARecordThatCannotBeReadIsKeptAsideBeforeAnythingWritesOverIt()
    {
        File.WriteAllText(Path_, "{ not json");
        var q = new ShaderQuarantine(Path_);
        Assert.Empty(q.All);
        var aside = Assert.Single(Aside());
        Assert.Equal("{ not json", File.ReadAllText(aside));
        Assert.False(File.Exists(Path_));
        Assert.Contains("could not be read", q.LoadWords, StringComparison.Ordinal);
        Assert.Contains($"kept as {Path.GetFileName(aside)}", q.LoadWords, StringComparison.Ordinal);

        // A later add writes a record of its own; what could not be read is still where it was kept.
        Assert.True(q.Add(Entry("a")).Applied);
        Assert.Equal("{ not json", File.ReadAllText(aside));
        Assert.Equal([Entry("a")], new ShaderQuarantine(Path_).All.ToArray());

        // Not a record at all (no list of kinds) is kept aside the same way.
        File.WriteAllText(Path_, "{\"kinds\":[]}");
        var other = new ShaderQuarantine(Path_);
        Assert.Empty(other.All);
        Assert.Equal(2, Aside().Length);
        Assert.Contains("could not be read", other.LoadWords, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryWhoseReasonThisBuildDoesNotKnowStaysQuarantinedBesideAKnownOne()
    {
        File.WriteAllText(Path_, "{\"quarantine\":[{\"hash\":\"a\",\"kind\":\"k\",\"reason\":\"melted\",\"words\":\"w\",\"at\":5,\"build\":\"9.9.9\"},"
            + "{\"hash\":\"b\",\"kind\":\"l\",\"reason\":\"gpuTime\",\"words\":\"x\",\"at\":6,\"build\":\"0.2.0\"}]}");
        var q = new ShaderQuarantine(Path_);
        Assert.Equal("", q.LoadWords);
        Assert.Equal(["a", "b"], q.All.Select(e => e.KindHash));
        Assert.True(q.IsQuarantined("a", out var a));
        Assert.Equal(QuarantineReason.Unknown, a.Reason);
        Assert.Equal("melted", a.ReasonWord);
        Assert.True(q.IsQuarantined("b", out var b));
        Assert.Equal("gpuTime", b.ReasonWord);
        Assert.Empty(Aside());

        // Written again, the unknown word is kept as it was.
        Assert.True(q.Add(Entry("c")).Applied);
        Assert.Equal("melted", Assert.Single(new ShaderQuarantine(Path_).All, e => e.KindHash == "a").ReasonWord);
    }

    [Fact]
    public void AnEntryWithNoHashIsCountedAndTheRecordIsCopiedAsideWhileTheRestIsHeld()
    {
        File.WriteAllText(Path_, "{\"quarantine\":[{\"kind\":\"no hash\"},{\"hash\":\"b\",\"kind\":\"l\",\"reason\":\"gpuTime\",\"words\":\"x\",\"at\":6,\"build\":\"0.2.0\"}]}");
        var text = File.ReadAllText(Path_);
        var q = new ShaderQuarantine(Path_);
        Assert.Equal(["b"], q.All.Select(e => e.KindHash));
        Assert.Contains("1 entry of the quarantine record", q.LoadWords, StringComparison.Ordinal);
        var aside = Assert.Single(Aside());
        Assert.Equal(text, File.ReadAllText(aside));
        // The readable record stays in place, so a restart still holds what was read.
        Assert.Equal(text, File.ReadAllText(Path_));
    }

    [Fact]
    public void AChangeTheHomeRefusesToRecordChangesNothingInMemoryOrOnDisk()
    {
        var q = new ShaderQuarantine(Path_);
        Assert.True(q.Add(Entry("a")).Applied);
        var changes = 0;
        q.Changed += () => changes++;
        // Every write lands beside the record first: a folder in that place refuses it.
        Directory.CreateDirectory(Path_ + ".saving");

        var release = q.Release("a");
        Assert.Equal(QuarantineOutcome.NotWritten, release.Outcome);
        Assert.Contains("could not be written", release.Words, StringComparison.Ordinal);
        Assert.Empty(release.Entries);
        Assert.Equal(QuarantineOutcome.NotWritten, q.ReleaseAll().Outcome);
        Assert.Equal(QuarantineOutcome.NotWritten, q.Add(Entry("b")).Outcome);
        Assert.Equal(["a"], q.All.Select(e => e.KindHash));
        Assert.Equal(["a"], q.Hashes);
        Assert.Equal(0, changes);
        Assert.Equal(q.All.ToArray(), new ShaderQuarantine(Path_).All.ToArray());
    }

    [Fact]
    public void AnInMemoryQuarantineWritesNoFile()
    {
        var q = new ShaderQuarantine();
        Assert.True(q.Add(Entry("a")).Applied);
        Assert.Single(q.All);
        Assert.Empty(Directory.EnumerateFiles(_folder));
    }
}
