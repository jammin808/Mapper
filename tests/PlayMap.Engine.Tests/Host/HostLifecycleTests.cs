using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using PlayMap.Core.Bus;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Net;

namespace PlayMap.Engine.Tests.Host;

/// <summary>The host's life: a door whose port is taken stays closed and says so while the node runs on the other; a
/// node with no door refuses to start; the recovery record puts the show back after a restart; a new pairing token
/// never rides a line and unpairs the old one; the render host, the scanner and the extensions plug in where the host
/// says, start before the doors open and stop before the engine; and the process entry answers its exit codes.</summary>
public sealed class HostLifecycleTests
{
    /// <summary>A port held by someone else for the length of a test.</summary>
    private static TcpListener Hold()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return l;
    }

    private static int Port(TcpListener l) => ((IPEndPoint)l.LocalEndpoint).Port;

    [Fact]
    public async Task APortInUseClosesThatDoorAndSaysSoInState()
    {
        var held = Hold();
        try
        {
            await using var rig = await HostRig.StartAsync(settings: s => s with { WirePort = Port(held) });
            Assert.Equal(0, rig.Network.WirePort);
            Assert.True(rig.Network.HttpPort > 0);
            var fault = Assert.Single(rig.State().GetProperty("faults").EnumerateArray());
            Assert.Equal("wire", fault.GetProperty("part").GetString());
            Assert.Contains("the line wire stays closed", fault.GetProperty("words").GetString(), StringComparison.Ordinal);
            Assert.Equal("red", rig.State().GetProperty("light").GetString());

            // The web port carries on: the node is reachable and every verb still answers there.
            using var http = rig.Http();
            using var reply = await http.PostAsync("/api/cmd", new StringContent("PLAYLIST NEW Show"));
            Assert.True(reply.IsSuccessStatusCode);
            Assert.Contains("the line wire stays closed", rig.Journal(), StringComparison.Ordinal);
        }
        finally
        {
            held.Stop();
        }
    }

    [Fact]
    public async Task ANodeWithNoDoorRefusesToStartAndSaysWhy()
    {
        var wire = Hold();
        var web = Hold();
        try
        {
            var ex = await Assert.ThrowsAsync<BindRefusedException>(() => HostRig.StartAsync(settings: s => s with { WirePort = Port(wire), HttpPort = Port(web) }));
            Assert.Contains("neither the line wire nor the web port", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            wire.Stop();
            web.Stop();
        }
    }

    [Fact]
    public async Task ARestartedNodeIsPlayingWhereTheShowIsNow()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-host-");
        var recover = (string home) => HostRig.Parse("engine", "headless", "--home", home);
        var first = await HostRig.StartAsync(options: recover, dir: dir);
        var list = await first.OkAsync("PLAYLIST NEW Show");
        var card = await first.OkAsync("GENERATOR NEW testcard Card");
        await first.OkAsync($"PLAYLIST ADD {list} generator:{card} Opening");
        await first.OkAsync($"PLAYLIST GO {list}");
        await first.OkAsync("BLACKOUT ON");
        await first.StopAsync();   // the folder stays: the next node starts from it
        Assert.True(File.Exists(first.Home.RecoveryPath));
        Assert.Contains("stopped; the recovery record holds the show", await File.ReadAllTextAsync(first.Home.JournalPath), StringComparison.Ordinal);

        await using var second = await HostRig.StartAsync(options: recover, dir: dir);
        var state = second.State();
        Assert.True(state.GetProperty("air").GetProperty("blackout").GetBoolean());
        var playlist = Assert.Single(state.GetProperty("playlists").EnumerateArray());
        Assert.Equal(list, playlist.GetProperty("id").GetString());
        Assert.Equal("playing", playlist.GetProperty("state").GetString());
        Assert.Contains("recovery: recovered", second.Journal(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairingNewKeepsTheTokenOffTheLineAndUnpairsTheOldOne()
    {
        await using var rig = await HostRig.StartAsync(settings: s => s with { TrustLoopback = false });
        await using var c = await rig.WireAsync();
        var nonce = (await c.ReadAsync())!;
        Assert.StartsWith("NONCE ", nonce, StringComparison.Ordinal);
        Assert.Equal("OK paired", await c.AskAsync("AUTH-PROOF " + Pairing.Proof(HostRig.Token, nonce[6..], "")));

        var reply = await c.AskAsync("@n PAIRING NEW");
        Assert.StartsWith("OK @n a new pairing token is in this node's settings", reply, StringComparison.Ordinal);
        var token = rig.Home.LoadSettings("unused").Token;
        Assert.NotEqual(Pairing.Normalise(HostRig.Token), Pairing.Normalise(token));
        Assert.DoesNotContain(Pairing.Normalise(token), Pairing.Normalise(reply!), StringComparison.Ordinal);
        Assert.DoesNotContain(token, rig.Engine.State().Json, StringComparison.Ordinal);
        Assert.DoesNotContain(token, rig.Journal(), StringComparison.Ordinal);

        // Paired with the old token: no longer. A proof of the new one pairs again.
        Assert.StartsWith("ERR @b not paired", await c.AskAsync("@b BLACKOUT ON"), StringComparison.Ordinal);
        var fresh = (await c.AskAsync("NONCE"))!;
        Assert.Equal("OK paired", await c.AskAsync("AUTH-PROOF " + Pairing.Proof(token, fresh[3..], "")));
        Assert.Equal("OK @c", await c.AskAsync("@c BLACKOUT ON"));
    }

    [Fact]
    public async Task ARenderHostAndAScannerPlugInAndAFailingRenderHostLeavesAHeadlessNode()
    {
        var render = new FakeRenderHost();
        IScanDisplay? displayed = null;
        await using (var rig = await HostRig.StartAsync(parts: new EngineParts
        {
            RenderHost = _ => render,
            ScanBackend = r =>
            {
                displayed = r.Display;
                return NullBackends.Instance;
            },
        }))
        {
            Assert.Same(render, displayed);
            Assert.Same(rig.Engine, render.StartedWith);
            Assert.Equal("Fake GL 3.3", rig.State().GetProperty("machine").GetProperty("renderer").GetString());
        }
        Assert.True(render.Disposed);

        await using var headless = await HostRig.StartAsync(parts: new EngineParts { RenderHost = _ => throw new InvalidOperationException("no GL on this machine") });
        Assert.Equal("none", headless.State().GetProperty("machine").GetProperty("renderer").GetString());
        var fault = Assert.Single(headless.State().GetProperty("faults").EnumerateArray());
        Assert.Equal("render", fault.GetProperty("part").GetString());
        Assert.Contains("no GL on this machine", fault.GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.True((await headless.RunAsync("PLAYLIST NEW Show")).Ok);
    }

    [Fact]
    public async Task AnExtensionAddsServicesStartsBeforeTheDoorsAndStopsBeforeTheEngine()
    {
        var calls = new ConcurrentQueue<string>();
        var queue = new FakeQueue();
        TestExtension? made = null;
        var parts = new EngineParts
        {
            Extensions =
            [
                setup =>
                {
                    setup.Approvals.Use(queue);
                    return made = new TestExtension(setup.Network, calls);
                },
                _ => throw new InvalidOperationException("this part is not here"),
            ],
        };
        await using (var rig = await HostRig.StartAsync(parts: parts))
        {
            Assert.Equal(["configure", "start with the doors closed"], calls);
            Assert.Equal(3, rig.State().GetProperty("ai").GetProperty("proposals").GetInt32());
            var fault = Assert.Single(rig.State().GetProperty("faults").EnumerateArray());
            Assert.Contains("this part is not here", fault.GetProperty("words").GetString(), StringComparison.Ordinal);

            // MCP asks the approval hook before a high-risk line: the extension's queue takes it, nothing runs.
            using var http = rig.Http();
            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            }));
            // MCP's server info names the build the node runs.
            Assert.Equal("PlayMap", mcp.ServerInfo.Name);
            Assert.Equal(Core.BuildInfo.Current.Informational, mcp.ServerInfo.Version);
            var run = await mcp.CallToolAsync("run_command", new Dictionary<string, object?> { ["line"] = "BLACKOUT ON", ["reason"] = "a test asks" });
            var text = string.Concat(run.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text));
            using (var receipt = JsonDocument.Parse(text))
            {
                Assert.Equal("queued", receipt.RootElement.GetProperty("stage").GetString());
                Assert.Equal("p1", receipt.RootElement.GetProperty("proposal").GetString());
            }
            Assert.Equal("BLACKOUT ON", Assert.Single(queue.Lines));
            Assert.False(rig.State().GetProperty("air").GetProperty("blackout").GetBoolean());
        }
        Assert.Equal(["configure", "start with the doors closed", "stop with the engine running"], calls);
        Assert.NotNull(made);
    }

    [Fact]
    public async Task TheEntryAnswersItsExitCodes()
    {
        // Bad arguments: 2, and the log in the node's folder says why.
        var badHome = Directory.CreateTempSubdirectory("playmap-entry-");
        try
        {
            var bad = new KeptLog();
            Assert.Equal(EngineEntry.BadArguments, await EngineEntry.RunAsync(["engine", "--home", badHome.FullName, "--bogus"], EngineParts.Bare, CancellationToken.None, bad));
            Assert.Contains(bad.Lines, l => l.Contains("unknown option --bogus", StringComparison.Ordinal));
            Assert.StartsWith(Core.BuildInfo.Current.Display + ": the engine did not start", bad.Lines.First(), StringComparison.Ordinal);
            // The file the log started in a fresh folder: its heading names the build, then the line that says why.
            var file = File.ReadAllLines(Path.Combine(badHome.FullName, NodeLogProvider.FileName));
            Assert.EndsWith(" info PlayMap.Engine.Log: " + NodeLogProvider.Heading, file[0], StringComparison.Ordinal);
            Assert.Contains(" error PlayMap.Engine: " + Core.BuildInfo.Current.Display + ": the engine did not start", file[1], StringComparison.Ordinal);
        }
        finally
        {
            badHome.Delete(recursive: true);
        }

        // Both doors taken: 3.
        var wire = Hold();
        var web = Hold();
        var refusedHome = Directory.CreateTempSubdirectory("playmap-entry-");
        try
        {
            new NodeHome(refusedHome.FullName).SaveSettings(NodeSettings.CreateDefault("Refused") with { Bind = "127.0.0.1", WirePort = Port(wire), HttpPort = Port(web), Announce = false });
            var refused = new KeptLog();
            Assert.Equal(EngineEntry.BindRefused, await EngineEntry.RunAsync(["engine", "headless", "--home", refusedHome.FullName], EngineParts.Bare, CancellationToken.None, refused));
            Assert.Contains(refused.Lines, l => l.Contains("neither the line wire nor the web port", StringComparison.Ordinal));
        }
        finally
        {
            wire.Stop();
            web.Stop();
            refusedHome.Delete(recursive: true);
        }

        // Asked to stop: 0, with the recovery record written on the way out.
        var home = Directory.CreateTempSubdirectory("playmap-entry-");
        try
        {
            new NodeHome(home.FullName).SaveSettings(NodeSettings.CreateDefault("Entry") with { Bind = "127.0.0.1", WirePort = 0, HttpPort = 0, SyncPort = 0, Announce = false });
            var kept = new KeptLog();
            using var stop = new CancellationTokenSource();
            var run = EngineEntry.RunAsync(["engine", "headless", "--home", home.FullName, "--name", "Entry node"], EngineParts.Bare, stop.Token, kept);
            Assert.True(await Eventually.TrueAsync(() => kept.Lines.Any(l => l.Contains("Entry node", StringComparison.Ordinal) && l.Contains("is up", StringComparison.Ordinal)), 20000));
            await stop.CancelAsync();
            Assert.Equal(EngineEntry.Stopped, await run.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(File.Exists(Path.Combine(home.FullName, "playmap.recovery.json")));
            Assert.Contains(kept.Lines, l => l.Contains("asked to stop", StringComparison.Ordinal));
            // The log's first line and the journal's start row name the build.
            Assert.StartsWith(Core.BuildInfo.Current.Display + ": the engine starts headless", kept.Lines.First(), StringComparison.Ordinal);
            static string TextOf(string row)
            {
                using var doc = JsonDocument.Parse(row);
                return doc.RootElement.GetProperty("text").GetString() ?? "";
            }
            var up = File.ReadLines(new NodeHome(home.FullName).JournalPath).Select(TextOf)
                .Single(t => t.StartsWith("Entry node is up:", StringComparison.Ordinal));
            Assert.EndsWith(" · " + Core.BuildInfo.Current.Display, up, StringComparison.Ordinal);
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    /// <summary>A render host as the stage side's would be: started with the engine, disposed with the host.</summary>
    private sealed class FakeRenderHost : IRenderHost
    {
        public EngineCore? StartedWith { get; private set; }

        public bool Disposed { get; private set; }

        public string RendererName => "Fake GL 3.3";

        public IReadOnlyList<DisplayInfo> Displays() => [];

        public IReadOnlyList<OutputHealth> Health() => [];

        public Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct) => Task.FromResult(0L);

        public void Release()
        {
            // Nothing to give back.
        }

        public void Start(EngineCore engine) => StartedWith = engine;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedCount(int n) : IProposalCount
    {
        public int Waiting => n;
    }

    /// <summary>An approval queue that takes every line and answers its id.</summary>
    private sealed class FakeQueue : IProposalSink
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ValueTask<string?> ProposeAsync(string line, string reason, VerbRisk risk, CancellationToken ct)
        {
            Lines.Enqueue(line);
            return ValueTask.FromResult<string?>("p" + Lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>An extension that says what it saw at each step of its life.</summary>
    private sealed class TestExtension(NetworkHost network, ConcurrentQueue<string> calls) : IEngineExtension
    {
        private EngineCore? _engine;

        public string Name => "test";

        public EngineServices Configure(EngineServices services)
        {
            calls.Enqueue("configure");
            return services with { Proposals = new FixedCount(3) };
        }

        public Task StartAsync(EngineCore engine, CancellationToken ct)
        {
            _engine = engine;
            calls.Enqueue(network.WirePort == 0 && network.HttpPort == 0 ? "start with the doors closed" : "start with a door open");
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            var alive = _engine is not null && await _engine.Lane.RunAsync(() => true).WaitAsync(TimeSpan.FromSeconds(5));
            calls.Enqueue(alive ? "stop with the engine running" : "stop after the engine");
        }
    }
}
