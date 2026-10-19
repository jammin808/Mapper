namespace PlayMap.Supervisor.Tests;

/// <summary>The supervisor's entry does nothing yet and says so: whatever its command line, it starts and watches no engine and answers
/// the exit code of a run that did nothing (never success, so no caller reads the run as a supervised engine), with its words; and it
/// references no PlayMap project but Core.</summary>
public sealed class SupervisorEntryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("install")]
    [InlineData("preinstall-check")]
    [InlineData("status")]
    [InlineData("resume")]
    public void EveryCommandLineIsAnsweredNothingDone(string word)
    {
        string[] args = word.Length == 0 ? [] : [word];
        var exit = Program.Main(args);
        Assert.Equal(Program.NothingDone, exit);
        Assert.NotEqual(0, exit);
        Assert.Equal("PlayMap.Supervisor does nothing yet: it starts and watches no engine and changes nothing on this machine", Program.Words);
    }

    [Fact]
    public void TheSupervisorReferencesNoPlayMapProjectButCore()
    {
        var playMap = typeof(Program).Assembly.GetReferencedAssemblies().Select(a => a.Name!)
            .Where(n => n.StartsWith("PlayMap.", StringComparison.Ordinal)).ToList();
        Assert.All(playMap, n => Assert.Equal("PlayMap.Core", n));
        Assert.Equal("PlayMap.Supervisor", typeof(Program).Assembly.GetName().Name);
        Assert.Equal(typeof(Program), typeof(Program).Assembly.EntryPoint?.DeclaringType);
    }
}
