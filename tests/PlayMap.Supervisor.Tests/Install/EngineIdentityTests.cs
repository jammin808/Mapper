using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Tests.Install;

/// <summary>The rule every reader of <c>engine.pid</c> holds the process it names to before it acts on it: PlayMap.exe run as the
/// engine, started no later than its record and not long before it (a pid reused after an unclean stop fails here), and, where the
/// caller asks, the installed exe, the show user and the show user's session. Pure: the facts are given.</summary>
public sealed class EngineIdentityTests
{
    private const long Stamped = 1_760_600_000_000;
    private const string Exe = @"C:\Program Files\PlayMap\PlayMap.exe";
    private const string Sid = "S-1-5-21-1000-2000-3000-1001";

    private static readonly EnginePidRecord Record = new(4120, 9941, Stamped);

    private static ProcessFacts Engine(string name = "PlayMap", string image = Exe, long started = Stamped - 3_000,
        string line = "\"" + Exe + "\" engine --supervised", string sid = Sid, int session = 1) =>
        new(true, name, image, started, line, sid, session, 900);

    private static readonly EngineExpectation Installed = new(Exe, Sid, 1);

    [Fact]
    public void TheRecordsEngineIsTrustedByEveryCaller()
    {
        Assert.Null(EngineIdentity.Refusal(Record, Engine(), EngineExpectation.AnyEngine));
        Assert.Null(EngineIdentity.Refusal(Record, Engine(), Installed));
        // The host stamps its record after the process starts: up to two minutes before is the same engine.
        Assert.Null(EngineIdentity.Refusal(Record, Engine(started: Stamped - 119_000), Installed));
        Assert.Null(EngineIdentity.Refusal(Record, Engine(image: @"c:\program files\playmap\playmap.exe"), Installed));
    }

    [Fact]
    public void AProcessThatIsNotPlayMapsEngineIsRefusedWithWhy()
    {
        Assert.Equal("pid 4120 is not running", EngineIdentity.Refusal(Record, ProcessFacts.Gone, EngineExpectation.AnyEngine));
        Assert.Equal("pid 4120 is notepad, not PlayMap.exe",
            EngineIdentity.Refusal(Record, Engine(name: "notepad", image: @"C:\Windows\notepad.exe", line: "notepad.exe"), EngineExpectation.AnyEngine));
        Assert.Equal("pid 4120 is PlayMap.exe run as the desk or a worker, not as the engine",
            EngineIdentity.Refusal(Record, Engine(line: "\"" + Exe + "\""), EngineExpectation.AnyEngine));
        Assert.Equal("pid 4120's command line cannot be read",
            EngineIdentity.Refusal(Record, Engine(line: ""), EngineExpectation.AnyEngine));
        Assert.Equal("pid 4120's start cannot be read",
            EngineIdentity.Refusal(Record, Engine(started: -1), EngineExpectation.AnyEngine));
    }

    [Fact]
    public void APidReusedSinceTheRecordWasWrittenIsRefused()
    {
        Assert.Equal("pid 4120 started after its record was written: another process has taken the pid",
            EngineIdentity.Refusal(Record, Engine(started: Stamped + 60_000), EngineExpectation.AnyEngine));
        Assert.Equal("pid 4120 started 600 s before its record was written: it is not the engine the record names",
            EngineIdentity.Refusal(Record, Engine(started: Stamped - 600_000), EngineExpectation.AnyEngine));
    }

    [Fact]
    public void ACallerThatActsWithHighRightsAsksForTheInstalledExeTheShowUserAndTheirSession()
    {
        Assert.Equal(@"pid 4120 runs C:\Users\show\PlayMap\PlayMap.exe, not the installed C:\Program Files\PlayMap\PlayMap.exe",
            EngineIdentity.Refusal(Record, Engine(image: @"C:\Users\show\PlayMap\PlayMap.exe"), Installed));
        Assert.Equal("pid 4120 does not run as the show user",
            EngineIdentity.Refusal(Record, Engine(sid: "S-1-5-21-1000-2000-3000-1002"), Installed));
        Assert.Equal("pid 4120 does not run as the show user",
            EngineIdentity.Refusal(Record, Engine(sid: ""), Installed));
        Assert.Equal("pid 4120 runs in session 3, not the show user's session 1",
            EngineIdentity.Refusal(Record, Engine(session: 3), Installed));
        // Any engine is enough where nothing is ended on the record's word (the armed check asks it, and nothing more).
        Assert.Null(EngineIdentity.Refusal(Record, Engine(image: @"C:\Users\show\PlayMap\PlayMap.exe", sid: "", session: 3),
            EngineExpectation.AnyEngine));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\PlayMap\\PlayMap.exe\" engine --supervised", true)]
    [InlineData("C:\\PlayMap\\PlayMap.exe engine headless --http 9940", true)]
    [InlineData("PlayMap.exe ENGINE", true)]
    [InlineData("\"C:\\Program Files\\PlayMap\\PlayMap.exe\"", false)]
    [InlineData("PlayMap.exe optimise-worker", false)]
    [InlineData("PlayMap.exe enginex", false)]
    [InlineData("\"C:\\engine dir\\PlayMap.exe\" --project engine.playmap", false)]
    [InlineData("", false)]
    public void TheEnginesCommandLineHasEngineAsItsFirstWord(string line, bool engine) =>
        Assert.Equal(engine, EngineIdentity.IsEngineLine(line));

    [Fact]
    public void ThisTestProcessIsNoEngine()
    {
        // The facts this OS gives of a live process that is not PlayMap.exe: the rule refuses it.
        var facts = ProcessReader.Read(Environment.ProcessId);
        Assert.True(facts.Exists);
        Assert.True(facts.StartedUnixMs > 0);
        var record = new EnginePidRecord(Environment.ProcessId, 9941, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.NotNull(EngineIdentity.Refusal(record, facts, EngineExpectation.AnyEngine));
        Assert.False(EngineIdentity.NamesRunningEngine(record));
    }
}
