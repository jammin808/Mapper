using PlayMap.Supervisor.Tests.Install;

namespace PlayMap.Supervisor.Tests;

/// <summary>The supervisor's command line: a line it does not take is answered with its commands and the usage code; off Windows every
/// machine command changes nothing and says so; the check and the version rule answer on every OS. It references no PlayMap project
/// but Core.</summary>
public sealed class SupervisorEntryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("frobnicate")]
    [InlineData("version-check", "0.2.1")]
    public async Task ALineItDoesNotTakeIsAnsweredWithItsCommands(params string[] args)
    {
        string[] line = args.Length == 1 && args[0].Length == 0 ? [] : args;
        using var said = new StringWriter();
        Assert.Equal(Program.Usage, await Program.RunAsync(line, said));
        Assert.Contains(Program.Words, said.ToString(), StringComparison.Ordinal);
    }

    [LinuxFact]
    public async Task OffWindowsEveryMachineCommandChangesNothingAndSaysSo()
    {
        foreach (var word in new[] { "run", "status", "resume", "stop", "install", "uninstall", "grant", "revoke", "session-probe" })
        {
            using var said = new StringWriter();
            Assert.Equal(Program.NotHere, await Program.RunAsync([word], said));
            Assert.Equal($"the supervisor runs on Windows only: '{word}' changes nothing here", said.ToString().Trim());
        }
    }

    [Fact]
    public async Task TheWordsAlsoGoIntoTheOutFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "playmap-words-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            using var said = new StringWriter();
            Assert.Equal(Program.Refused, await Program.RunAsync(["version-check", "0.2.1", "0.2.500", "--out", file], said));
            Assert.Equal(said.ToString(), await File.ReadAllTextAsync(file));
        }
        finally
        {
            File.Delete(file);
        }
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
