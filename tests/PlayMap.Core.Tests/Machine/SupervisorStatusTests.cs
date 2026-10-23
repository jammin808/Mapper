using PlayMap.Core.Machine;

namespace PlayMap.Core.Tests.Machine;

/// <summary>The supervisor's status file, read by the engine and written by the supervisor through this one record: its keys in their
/// order, read back as written, written whole into a folder and read from it, and any text that is not a status reads null.</summary>
public sealed class SupervisorStatusTests
{
    private static readonly SupervisorStatus Running =
        new("running", 4120, 2, "frozen: shown frames stood at 600 for 3 s while the GPU ran", "active", 1760600000000);

    [Fact]
    public void AStatusReadsBackAsWrittenWithItsKeysInTheFilesOrder()
    {
        Assert.Equal(
            "{\"state\":\"running\",\"pid\":4120,\"restarts\":2,\"lastCause\":\"frozen: shown frames stood at 600 for 3 s while the GPU ran\","
            + "\"freezeRule\":\"active\",\"at\":1760600000000}", Running.ToJson());
        Assert.Equal(Running, SupervisorStatus.Parse(Running.ToJson()));
        // The words are optional: a status without them reads them empty.
        Assert.Equal(new SupervisorStatus("starting", 0, 0, "", "", 1), SupervisorStatus.Parse("{\"state\":\"starting\",\"pid\":0,\"restarts\":0,\"at\":1}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("[]")]
    [InlineData("{\"state\":\"running\"}")]
    [InlineData("{\"pid\":4120,\"restarts\":0,\"at\":1}")]
    [InlineData("{\"state\":7,\"pid\":4120,\"restarts\":0,\"at\":1}")]
    [InlineData("{\"state\":\"running\",\"pid\":-1,\"restarts\":0,\"at\":1}")]
    [InlineData("{\"state\":\"running\",\"pid\":4120,\"restarts\":-2,\"at\":1}")]
    [InlineData("{\"state\":\"running\",\"pid\":\"4120\",\"restarts\":0,\"at\":1}")]
    [InlineData("{\"state\":\"running\",\"pid\":4120.5,\"restarts\":0,\"at\":1}")]
    [InlineData("{\"state\":\"running\",\"pid\":4120,\"restarts\":0}")]
    [InlineData("{\"state\":\"running\",\"pid\":4120,\"restarts\":0,\"at\":\"now\"}")]
    [InlineData("{\"state\":\"running\",\"pid\":4120,\"restarts\":0,\"at\":1,\"x\":{\"y\":{\"z\":1}}}")]
    [InlineData("{\"state\":")]
    public void ATextThatIsNotAStatusReadsNull(string? text) => Assert.Null(SupervisorStatus.Parse(text));

    [Fact]
    public void ATooLongTextReadsNull()
    {
        Assert.Null(SupervisorStatus.Parse(new string('x', SupervisorStatus.MaxBytes + 1)));
        var padded = Running with { LastCause = new string('c', SupervisorStatus.MaxBytes) };
        Assert.Null(SupervisorStatus.Parse(padded.ToJson()));
    }

    [Fact]
    public void ItIsWrittenWholeIntoTheFolderAndReadFromIt()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-status-").FullName;
        try
        {
            Assert.Null(SupervisorStatus.Read(folder));
            Running.Write(folder);
            var path = Path.Combine(folder, SupervisorStatus.FileName);
            Assert.Equal([path], Directory.GetFiles(folder));
            Assert.Equal(Running, SupervisorStatus.Read(folder));
            var held = Running with { State = "held", Pid = 0, LastCause = "a person stopped the engine" };
            held.Write(folder);
            Assert.Equal(held, SupervisorStatus.Read(folder));
            File.WriteAllText(path, "not a status");
            Assert.Null(SupervisorStatus.Read(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
