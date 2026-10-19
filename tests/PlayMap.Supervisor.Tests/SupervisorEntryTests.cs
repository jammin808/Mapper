namespace PlayMap.Supervisor.Tests;

/// <summary>The supervisor's entry does nothing yet and says so: whatever its command line, it starts and watches no engine, answers
/// the exit code of a run that did nothing (never success, so no caller reads the run as a supervised engine) and puts its words on
/// the standard error; and it references no PlayMap project but Core.</summary>
[Collection(nameof(ConsoleCollection))]
public sealed class SupervisorEntryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("install")]
    [InlineData("preinstall-check")]
    [InlineData("status")]
    [InlineData("resume")]
    public void EveryCommandLineIsAnsweredNothingDoneWithTheWords(string word)
    {
        string[] args = word.Length == 0 ? [] : [word];
        var before = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        int exit;
        try
        {
            exit = Program.Main(args);
        }
        finally
        {
            Console.SetError(before);
        }
        Assert.Equal(Program.NothingDone, exit);
        Assert.NotEqual(0, exit);
        Assert.Equal(Program.Words, error.ToString().TrimEnd());
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

/// <summary>The tests that swap the process's standard error run one at a time.</summary>
[CollectionDefinition(nameof(ConsoleCollection), DisableParallelization = true)]
public sealed class ConsoleCollection;
