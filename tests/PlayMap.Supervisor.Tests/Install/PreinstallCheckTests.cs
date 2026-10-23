using System.Net;
using System.Net.Sockets;
using System.Text;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Tests.Install;

/// <summary>The installer's armed check fails closed: the decision is pure, and the look runs against fake engines on loopback (a pid
/// record naming this test process, which the look is told is the engine, and a listener answering as the engine would): no engine
/// process → go on; an engine answering armed → refused with its words; disarmed → go on; an engine process alive but silent →
/// refused, naming its pid; a pid record whose process is gone, or whose process is not PlayMap's engine, or a record file grown past
/// a record's length, → go on.</summary>
public sealed class PreinstallCheckTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(400);

    /// <summary>The fakes declare themselves: the record names this test process, which the look takes for the engine.</summary>
    private static bool Declared(EnginePidRecord record) => record.Pid == Environment.ProcessId;

    [Fact]
    public void TheDecisionIsPure()
    {
        var utc = TimeZoneInfo.Utc;
        Assert.Equal(new PreinstallVerdict(true, "no engine runs on this machine: the install goes on"),
            PreinstallCheck.Decide(new PreinstallFacts(EngineFinding.NoEngine, 0, "", 0), utc));
        Assert.Equal(new PreinstallVerdict(true, "the engine (pid 4120) is not armed: the install goes on"),
            PreinstallCheck.Decide(new PreinstallFacts(EngineFinding.Disarmed, 4120, "", 0), utc));
        var since = new DateTimeOffset(2026, 10, 19, 20, 31, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal(new PreinstallVerdict(false, "the show is armed on this node since 20:31 by Desk: disarm first"),
            PreinstallCheck.Decide(new PreinstallFacts(EngineFinding.Armed, 4120, "Desk", since), utc));
        Assert.Equal(new PreinstallVerdict(false,
                "an engine is running here (pid 4120) but does not answer: stop it from the desk or with PlayMap.Supervisor.exe stop, then install again"),
            PreinstallCheck.Decide(new PreinstallFacts(EngineFinding.Silent, 4120, "", 0), utc));
    }

    [Fact]
    public void ShowsAnswerIsReadForArmedAndDisarmedAndNothingElse()
    {
        Assert.Equal(new PreinstallFacts(EngineFinding.Armed, 9, "Desk", 1760000000000),
            PreinstallCheck.ReadShow(9, "{\"armed\":true,\"by\":\"Desk\",\"since\":1760000000000,\"words\":\"\"}"));
        Assert.Equal(new PreinstallFacts(EngineFinding.Disarmed, 9, "", 0), PreinstallCheck.ReadShow(9, "{\"armed\":false}"));
        Assert.Null(PreinstallCheck.ReadShow(9, "{\"name\":\"x\"}"));
        Assert.Null(PreinstallCheck.ReadShow(9, "not json"));
    }

    [Fact]
    public async Task NoEngineProcessGoesOn()
    {
        using var folder = new Folder();
        var facts = await PreinstallCheck.LookAsync(folder.Path, () => null, Declared, Quick);
        Assert.Equal(EngineFinding.NoEngine, facts.Finding);
        Assert.True(PreinstallCheck.Decide(facts, TimeZoneInfo.Utc).GoOn);
    }

    [Fact]
    public async Task APidRecordWhoseProcessIsGoneGoesOn()
    {
        using var folder = new Folder();
        Assert.True(new EnginePidRecord(2_147_480_000, 9941, 1).TryWrite(folder.Path));
        var facts = await PreinstallCheck.LookAsync(folder.Path, () => null, EngineIdentity.NamesRunningEngine, Quick);
        Assert.Equal(EngineFinding.NoEngine, facts.Finding);
    }

    [Fact]
    public async Task ARecordWhosePidIsAliveButIsNotTheEngineGoesOn()
    {
        // The record names this test process, which is no PlayMap engine, and a listener that would answer armed: the record is passed
        // over, so nothing it names is asked, and with no engine found the install goes on.
        using var folder = new Folder();
        await using var engine = FakeEngine.Answering("{\"armed\":true,\"by\":\"Desk\",\"since\":1,\"words\":\"armed\"}");
        Assert.True(new EnginePidRecord(Environment.ProcessId, engine.Port, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).TryWrite(folder.Path));
        var facts = await PreinstallCheck.LookAsync(folder.Path, () => null, EngineIdentity.NamesRunningEngine, Quick);
        Assert.Equal(new PreinstallFacts(EngineFinding.NoEngine, 0, "", 0), facts);
        Assert.Equal(0, engine.Asked);
    }

    [Fact]
    public async Task ARecordFileGrownPastARecordIsPassedOverUnread()
    {
        // The engine's folder is the show user's to write: a record file grown to a gigabyte is no record, refused by its length, and
        // the look goes on to the engines it finds itself.
        using var folder = new Folder();
        using (var grown = new FileStream(System.IO.Path.Combine(folder.Path, EnginePidRecord.FileName), FileMode.Create, FileAccess.Write))
            grown.SetLength(1L << 30);
        var facts = await PreinstallCheck.LookAsync(folder.Path, () => null, Declared, Quick);
        Assert.Equal(new PreinstallFacts(EngineFinding.NoEngine, 0, "", 0), facts);
    }

    [Fact]
    public async Task AnEngineAnsweringArmedIsRefusedWithItsWords()
    {
        using var folder = new Folder();
        var since = new DateTimeOffset(2026, 10, 19, 20, 31, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        await using var engine = FakeEngine.Answering($"{{\"armed\":true,\"by\":\"Desk\",\"since\":{since},\"words\":\"armed\"}}");
        Assert.True(new EnginePidRecord(Environment.ProcessId, engine.Port, 1).TryWrite(folder.Path));
        var facts = await PreinstallCheck.LookAsync(folder.Path, () => null, Declared, TimeSpan.FromSeconds(5));
        Assert.Equal(new PreinstallFacts(EngineFinding.Armed, Environment.ProcessId, "Desk", since), facts);
        var verdict = PreinstallCheck.Decide(facts, TimeZoneInfo.Utc);
        Assert.False(verdict.GoOn);
        Assert.Equal("the show is armed on this node since 20:31 by Desk: disarm first", verdict.Words);
        Assert.Equal(1, engine.Asked);
        Assert.Equal("@pmsup SHOW STATUS", engine.LastLine);
    }

    [Fact]
    public async Task AnEngineAnsweringDisarmedGoesOn()
    {
        using var folder = new Folder();
        await using var engine = FakeEngine.Answering("{\"armed\":false,\"by\":\"\",\"since\":0}");
        Assert.True(new EnginePidRecord(Environment.ProcessId, engine.Port, 1).TryWrite(folder.Path));
        var facts = await PreinstallCheck.LookAsync(folder.Path, () => null, Declared, TimeSpan.FromSeconds(5));
        Assert.Equal(EngineFinding.Disarmed, facts.Finding);
        Assert.True(PreinstallCheck.Decide(facts, TimeZoneInfo.Utc).GoOn);
    }

    [Fact]
    public async Task AnEngineProcessAliveButSilentIsRefusedNamingItsPid()
    {
        using var folder = new Folder();
        await using var engine = FakeEngine.Silent();
        Assert.True(new EnginePidRecord(Environment.ProcessId, engine.Port, 1).TryWrite(folder.Path));
        var facts = await PreinstallCheck.LookAsync(folder.Path, () => null, Declared, Quick);
        Assert.Equal(new PreinstallFacts(EngineFinding.Silent, Environment.ProcessId, "", 0), facts);
        var verdict = PreinstallCheck.Decide(facts, TimeZoneInfo.Utc);
        Assert.False(verdict.GoOn);
        Assert.Contains($"(pid {Environment.ProcessId}) but does not answer", verdict.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEngineProcessWithNoListenerIsSilentNeverDisarmed()
    {
        using var folder = new Folder();
        // A port nothing listens on: the connection is refused, which is silence.
        var port = FreePort();
        var facts = await PreinstallCheck.LookAsync(folder.Path, () => (Environment.ProcessId, port), Declared, Quick);
        Assert.Equal(EngineFinding.Silent, facts.Finding);
        Assert.False(PreinstallCheck.Decide(facts, TimeZoneInfo.Utc).GoOn);
    }

    [Fact]
    public async Task AnErrAnswerIsRefusedQuotingTheEnginesReason()
    {
        using var folder = new Folder();
        await using var engine = FakeEngine.Refusing("not paired: this client holds no pairing");
        Assert.True(new EnginePidRecord(Environment.ProcessId, engine.Port, 1).TryWrite(folder.Path));
        var facts = await PreinstallCheck.LookAsync(folder.Path, () => null, Declared, TimeSpan.FromSeconds(5));
        Assert.Equal(new PreinstallFacts(EngineFinding.Refused, Environment.ProcessId, "", 0, "not paired: this client holds no pairing"), facts);
        var verdict = PreinstallCheck.Decide(facts, TimeZoneInfo.Utc);
        Assert.False(verdict.GoOn);
        Assert.Equal($"the engine (pid {Environment.ProcessId}) answered the armed check with ERR not paired: this client holds no pairing: "
            + "disarm or stop it from the desk, then install again", verdict.Words);
    }

    [Fact]
    public void ReplyLinesAreMatchedByTheirRequestIdOnly()
    {
        Assert.Equal("{}", LoopbackWire.Reply("OK @pmsup {}", "OK", "pmsup"));
        Assert.Equal("", LoopbackWire.Reply("OK @pmsup", "OK", "pmsup"));
        Assert.Null(LoopbackWire.Reply("OK @pmsup2 {}", "OK", "pmsup"));
        Assert.Null(LoopbackWire.Reply("STATE {}", "OK", "pmsup"));
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("playmap-preinstall-").FullName;

        public void Dispose() => Directory.Delete(Path, true);
    }
}
