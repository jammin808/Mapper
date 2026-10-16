using System.Text.Json;
using PlayMap.Core.Led;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;
using PlayMap.Engine.Tests.VideoIo;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>What a node puts on the network beside its own outputs, by its place in a hot-backup pair, two real nodes on loopback each
/// with an LED sender and an NDI sender the test reads: only the node that has the show sends. A hot backup's LED sender stops with no
/// black frame (its primary's frames are the rig's) and its send is never made, though the mirrored show has both; FAILOVER makes it
/// send; the fenced old primary's LED goes black once and stops, and its send goes dark (one black frame) and closes; FAILBACK swaps the
/// two again. STATE, the context and INDEX say standby (green) on the backup and held on the fenced node.</summary>
public sealed class BackupNetworkAirTests
{
    private const long Second = SyncRig.Second;

    [Fact]
    public async Task OnlyTheNodeWithTheShowSendsLedDataAndVideo()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        var ledA = new RecordingLed();
        var ledB = new RecordingLed();
        using var ndiA = new FakeVideoProvider(LiveProtocol.Ndi, clock);
        using var ndiB = new FakeVideoProvider(LiveProtocol.Ndi, clock);
        await using var primary = await StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time, ledA, ndiA);
        await using var backup = await StartAsync("FOH-B", BackupRig.BackupId, clock, time, ledB, ndiB);
        var show = SyncRig.Show() with
        {
            Leds = new LedRig { Nodes = [new LedNode("ln00000001", "Bar", LedProtocol.Ddp, "127.0.0.1", 0, true, 0, 0, LedSync.Latch, 510, "#FFFFFF")] },
        };
        await SyncRig.OpenShowAsync(primary, show);
        await primary.OkAsync("SEND ADD NDI Main output:op_1");
        Assert.True(ledA.Master);
        Assert.Single(ndiA.Sinks);
        Assert.True(ledB.Master);   // solo, its machine master on

        // The backup mirrors a show with an LED rig and a send: it sends neither, and its LED went quiet without a black frame.
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);
        await BackupRig.BackUpAsync(primary, backup);
        Assert.Equal(BackupRig.ShowJson(primary), BackupRig.ShowJson(backup));
        Assert.False(ledB.Master);
        Assert.DoesNotContain(ledB.Calls, c => c.BlackFirst);
        Assert.True(await Eventually.TrueAsync(() => SendState(backup) == "standby"), Sends(backup).ToString());
        Assert.Empty(ndiB.Sinks);
        Assert.Equal("standby", LedState(backup));
        Assert.Contains("hot backup of FOH-A", LedWords(backup), StringComparison.Ordinal);
        Assert.Equal("green", ContextLight(backup, "send:" + SendId(backup)));
        Assert.Equal("green", ContextLight(backup, "led:ln00000001"));
        // The primary sends all the while.
        Assert.True(ledA.Master);
        Assert.Equal(0, ndiA.Sinks[0].Closes);

        // FAILOVER: the new primary sends from now on; the old primary is fenced, its LED black once then stopped, its send dark and closed.
        var takeover = await backup.RunAsync("FAILOVER");
        Assert.True(takeover.Ok, takeover.Payload);
        Assert.True(ledB.Master);
        var sinkB = Assert.Single(ndiB.Sinks);
        Assert.NotEqual("standby", SendState(backup));
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Role(primary) == "fenced"), BackupRig.Fleet(primary) + "\n" + primary.Journal());
        Assert.Equal((false, true), ledA.Calls[^1]);
        Assert.True(await Eventually.TrueAsync(() => ndiA.Sinks[0].Closes == 1));
        Assert.True(ndiA.Sinks[0].BlackOnStop);
        Assert.Equal("held", SendState(primary));
        Assert.Equal("held", LedState(primary));
        Assert.Contains("fenced", LedWords(primary), StringComparison.Ordinal);

        // The fenced node mirrors the new primary: still silent, no new sender, no new black frame.
        var heldCalls = ledA.Calls.Count;
        await BackupRig.BackUpAsync(backup, primary);
        Assert.False(ledA.Master);
        Assert.Equal(heldCalls, ledA.Calls.Count);
        Assert.Single(ndiA.Sinks);
        Assert.True(await Eventually.TrueAsync(() => SendState(primary) == "standby"), Sends(primary).ToString());

        // FAILBACK: the two swap — the original primary sends again; the node that held the show goes silent with no black LED frame.
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Fleet(backup).GetProperty("backups").EnumerateArray()
            .Any(b => b.GetProperty("node").GetString() == primary.Id && b.GetProperty("state").GetString() == "identical")), BackupRig.Fleet(backup).ToString());
        var back = await backup.RunAsync("FAILBACK CONFIRM");
        Assert.True(back.Ok, back.Payload);
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Role(primary) == "primary" && BackupRig.MirrorState(backup) == "identical", 15000),
            BackupRig.Fleet(primary) + "\n" + BackupRig.Fleet(backup) + "\n" + primary.State().GetProperty("sync") + "\n" + backup.State().GetProperty("sync")
            + $"\nA answers on {primary.Network.SyncPort}, B on {backup.Network.SyncPort}; B hears A's clock on {backup.Network.Hello!.Peers.Find(primary.Id)?.Hello.Port("sync")} from {backup.Network.Hello!.Peers.Find(primary.Id)?.Hello.Name} ({backup.Network.Hello!.Peers.Find(primary.Id)?.Liveness}); hello ports A {primary.Network.HelloPort} B {backup.Network.HelloPort}\n" + backup.Journal() + "\n---\n" + primary.Journal());
        Assert.True(ledA.Master);
        Assert.Equal(2, ndiA.Sinks.Count);
        Assert.Equal(0, ndiA.Sinks[1].Closes);
        Assert.False(ledB.Master);
        Assert.DoesNotContain(ledB.Calls, c => c.BlackFirst);
        Assert.True(await Eventually.TrueAsync(() => sinkB.Closes == 1));
        Assert.Single(ndiB.Sinks);
        Assert.Equal("standby", SendState(backup));
    }

    private static Task<HostRig> StartAsync(string name, string id, IMonoClock mono, ManualTime time, RecordingLed led, FakeVideoProvider ndi) =>
        HostRig.StartAsync(name, network: true, mono: mono, parts: NetworkEdges.With(BackupRig.Parts(time), led, ndi),
            settings: s => s with { NodeId = id, TrustLoopback = false, Led = s.Led with { Output = true } });

    private static JsonElement Sends(HostRig rig) => rig.State().GetProperty("videoio").GetProperty("sends");

    private static string SendId(HostRig rig) => Sends(rig)[0].GetProperty("id").GetString()!;

    private static string? SendState(HostRig rig) => Sends(rig).GetArrayLength() == 0 ? null : Sends(rig)[0].GetProperty("state").GetString();

    private static JsonElement LedNode(HostRig rig) => rig.State().GetProperty("led").GetProperty("nodes")[0];

    private static string? LedState(HostRig rig) => LedNode(rig).GetProperty("state").GetString();

    private static string LedWords(HostRig rig) => LedNode(rig).GetProperty("why").GetString() ?? "";

    private static string? ContextLight(HostRig rig, string id) =>
        JsonDocument.Parse(rig.Engine.Context()).RootElement.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("id").GetString() == id).Select(i => i.GetProperty("light").GetString()).FirstOrDefault();
}

/// <summary>The node's network edges for the test: the LED sender it reads and an NDI that makes the senders it reads; the video
/// hub ticks on its own thread as the node's part makes it.</summary>
internal sealed class NetworkEdges(RecordingLed led, FakeVideoProvider ndi) : IEngineExtension
{
    private EngineCore? _engine;

    /// <summary>The parts with these edges added last.</summary>
    public static EngineParts With(EngineParts parts, RecordingLed led, FakeVideoProvider ndi) =>
        parts with { Extensions = [.. parts.Extensions, _ => new NetworkEdges(led, ndi)] };

    public string Name => "test-edges";

    public EngineServices Configure(EngineServices services) =>
        services with { LedOutput = led, LiveInputs = [ndi], SendProviders = [ndi] };

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        _engine = engine;
        engine.VideoIo.Start();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _engine?.VideoIo.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>An LED sender that keeps every machine-master request (on, and whether a black frame goes first).</summary>
internal sealed class RecordingLed : ILedOutput
{
    private readonly Lock _gate = new();
    private readonly List<(bool On, bool BlackFirst)> _calls = [];

    public IReadOnlyList<(bool On, bool BlackFirst)> Calls
    {
        get
        {
            lock (_gate)
                return [.. _calls];
        }
    }

    public bool Master
    {
        get
        {
            lock (_gate)
                return _calls.Count > 0 && _calls[^1].On;
        }
    }

    public ILedFrameSink Sink { get; } = new LedFramePool();

    public void Apply(LedSendProgram program)
    {
        // The program is the sender's own business: this test reads the master only.
    }

    public void SetMaster(bool on, bool blackFirst)
    {
        lock (_gate)
            _calls.Add((on, blackFirst));
    }

    public void StopNode(string nodeId, bool blackFirst)
    {
        // As Apply.
    }

    public LedOutputView View() => Master ? new LedOutputView(true, [], 0, 0) : LedOutputView.Off;

    public bool TryMonitor(string nodeId, int universe, Span<byte> slots, out long leaderNs)
    {
        leaderNs = 0;
        return false;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
