using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PlayMap.Core.Bus;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;
using PlayMap.Net.Companion;
using PlayMap.Net.Http;
using PlayMap.Net.Wire;

namespace PlayMap.Net.Tests.Companion;

/// <summary>A line-wire client over a WebSocket (subprotocol playmap.lines.v1): one text frame per line.</summary>
internal sealed class WsLines : IAsyncDisposable
{
    private readonly byte[] _buffer = new byte[256 * 1024];
    private Task<string?>? _pending;

    public ClientWebSocket Socket { get; } = new();

    public List<string> Pushes { get; } = [];

    public static async Task<WsLines> ConnectAsync(int port)
    {
        var c = new WsLines();
        c.Socket.Options.AddSubProtocol(WebSocketLineChannel.SubProtocol);
        await c.Socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/ws"), CancellationToken.None);
        return c;
    }

    public Task SendAsync(string line) => Socket.SendAsync(Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The next line, or null once the node closed; throws on timeout.</summary>
    public async Task<string?> ReadAsync(int timeoutMs = 5000)
    {
        _pending ??= ReceiveAsync();
        var done = await Task.WhenAny(_pending, Task.Delay(timeoutMs));
        if (done != _pending)
            throw new TimeoutException("no line within " + timeoutMs + " ms");
        var line = await _pending;
        _pending = null;
        return line;
    }

    private async Task<string?> ReceiveAsync()
    {
        var count = 0;
        while (true)
        {
            var r = await Socket.ReceiveAsync(_buffer.AsMemory(count), CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close)
                return null;
            count += r.Count;
            if (r.EndOfMessage)
                return Encoding.UTF8.GetString(_buffer, 0, count);
        }
    }

    /// <summary>The next line that is not a push (pushes are kept).</summary>
    public async Task<string?> ReplyAsync(int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var line = await ReadAsync(Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds));
            if (line is not null && (line.StartsWith("STATE ", StringComparison.Ordinal) || line.StartsWith("CONTEXT ", StringComparison.Ordinal)
                || line.StartsWith("PROJECT ", StringComparison.Ordinal) || line.StartsWith("NONCE ", StringComparison.Ordinal)))
            {
                Pushes.Add(line);
                continue;
            }
            return line;
        }
    }

    public async Task<string?> AskAsync(string line, int timeoutMs = 5000)
    {
        await SendAsync(line);
        return await ReplyAsync(timeoutMs);
    }

    /// <summary>Reads until the node closes (lines are dropped): the close status it sent.</summary>
    public async Task<WebSocketCloseStatus?> ClosedAsync(int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (await ReadAsync(Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds)) is not null)
        {
            // Lines before the close are not what this waits for.
        }
        return Socket.CloseStatus;
    }

    /// <summary>NONCE (sent at connect), HELLO, then AUTH-PROOF with the token: true when the node says "paired".</summary>
    public async Task<bool> PairAsync(string name, string token)
    {
        var nonce = (await ReadAsync())!;
        Assert.StartsWith("NONCE ", nonce, StringComparison.Ordinal);
        Assert.Equal("OK", await AskAsync("HELLO " + name));
        var reply = await AskAsync("AUTH-PROOF " + Pairing.Proof(token, nonce[6..], name));
        return reply == "OK paired";
    }

    public async ValueTask DisposeAsync()
    {
        if (Socket.State == WebSocketState.Open)
        {
            try
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", bye.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // The node closed first.
                Assert.NotNull(ex);
            }
        }
        Socket.Dispose();
    }
}

/// <summary>The line wire over the web port's WebSocket: it pairs like TCP 9941 but answers nothing but pairing until it pairs (the
/// greeting STATE waits for "OK paired"); a name is a claim that only narrows (Companion is "companion", the desk's name on a WebSocket
/// is no desk); a binary frame closes 1003, a frame past 64 KiB 1009, five wrong proofs 4401; the JSON /ws without the subprotocol is
/// unchanged.</summary>
public sealed class LineWebSocketTests
{
    private static async Task<HttpHost> StartAsync(IWireEndpoint endpoint, NodeSettings settings, HttpHostOptions? options = null)
    {
        var host = new HttpHost(settings, endpoint, TestSettings.Wall(), (options ?? new HttpHostOptions { Mcp = false }) with { Port = 0 });
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task UnpairedItAnswersOnlyPairingThenPairsAndRuns()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: false));
        await using var ws = await WsLines.ConnectAsync(host.Port);
        Assert.Equal(WebSocketLineChannel.SubProtocol, ws.Socket.SubProtocol);

        var nonce = (await ws.ReadAsync())!;
        Assert.StartsWith("NONCE ", nonce, StringComparison.Ordinal);
        Assert.Equal("OK", await ws.AskAsync("HELLO Companion:FOH deck"));
        // No STATE before pairing, and nothing but pairing is answered: not a query, not a subscription, not an edit.
        Assert.StartsWith("ERR @q1 not paired", await ws.AskAsync("@q1 STATE"), StringComparison.Ordinal);
        Assert.StartsWith("ERR @s1 not paired", await ws.AskAsync("@s1 SUBSCRIBE CONTEXT"), StringComparison.Ordinal);
        Assert.StartsWith("ERR @b1 not paired", await ws.AskAsync("@b1 BLACKOUT ON"), StringComparison.Ordinal);
        Assert.StartsWith("ERR @a1 plain AUTH", await ws.AskAsync("@a1 AUTH " + TestSettings.Token), StringComparison.Ordinal);
        Assert.StartsWith("OK @p1 PONG", await ws.AskAsync("@p1 PING"), StringComparison.Ordinal);
        Assert.DoesNotContain(ws.Pushes, p => p.StartsWith("STATE ", StringComparison.Ordinal));
        Assert.Empty(endpoint.Lines);
        Assert.False(endpoint.Blackout);

        Assert.Equal("OK @c2 paired", await ws.AskAsync("@c2 AUTH-PROOF " + Pairing.Proof(TestSettings.Token, nonce[6..], "Companion:FOH deck")));
        var greeting = await ws.ReadAsync();
        Assert.StartsWith("STATE {", greeting, StringComparison.Ordinal);

        Assert.Equal("OK @c3", await ws.AskAsync("@c3 BLACKOUT ON"));
        Assert.True(endpoint.Blackout);
        var (line, session) = Assert.Single(endpoint.Lines);
        Assert.Equal("BLACKOUT", line.Verb);
        Assert.Equal("companion", session.Transport);
        Assert.Equal("Companion:FOH deck", session.ClientName);
        Assert.True(session.Paired);
        Assert.StartsWith("l", session.SessionId, StringComparison.Ordinal);
        Assert.Equal(1, host.LineSockets);
        Assert.Equal(0, host.WebSockets);
    }

    [Fact]
    public async Task AClaimNeverWidensRightsTheDeskNameOnTheWebPortIsNoDesk()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: false));
        await using var ws = await WsLines.ConnectAsync(host.Port);
        Assert.True(await ws.PairAsync(WireSession.DeskName, TestSettings.Token));
        Assert.Equal("OK", await ws.AskAsync("BLACKOUT ON"));
        var session = Assert.Single(endpoint.Lines).Session;
        Assert.Equal("ws", session.Transport);
        Assert.Equal(WireSession.DeskName, session.ClientName);
    }

    [Fact]
    public async Task ABinaryFrameCloses1003()
    {
        await using var host = await StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: true));
        await using var ws = await WsLines.ConnectAsync(host.Port);
        await ws.Socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, await ws.ClosedAsync());
    }

    [Fact]
    public async Task AFramePast64KiBCloses1009()
    {
        await using var host = await StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: true));
        await using var ws = await WsLines.ConnectAsync(host.Port);
        await ws.SendAsync("PRESENCE {\"where\":\"" + new string('x', 70 * 1024) + "\"}");
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await ws.ClosedAsync());
    }

    [Fact]
    public async Task FiveWrongProofsClose4401AndLockTheAddressOutOfTheWebPort()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: false));
        await using var ws = await WsLines.ConnectAsync(host.Port);
        var nonce = (await ws.ReadAsync())!;
        Assert.Equal("OK", await ws.AskAsync("HELLO Companion:FOH"));
        for (var i = 0; i < 5; i++)
        {
            var reply = await ws.AskAsync("AUTH-PROOF " + new string('0', 64));
            Assert.StartsWith("ERR wrong token", reply, StringComparison.Ordinal);
        }
        Assert.Equal((WebSocketCloseStatus)WebSocketLineChannel.WrongProofsClose, await ws.ClosedAsync());
        Assert.NotNull(nonce);

        // The address spent its wrong tokens: the web port's header gate refuses it too for the rest of the minute.
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + host.Port) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        request.Headers.Add("X-PlayMap-Token", TestSettings.Token);
        using var refused = await http.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task AWebPageFromAnotherSiteIsRefusedTheLineChannelEvenOnATrustedAddress()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        var uri = new Uri("ws://127.0.0.1:" + host.Port + "/ws");

        // A page the node did not serve: a browser says where it came from, and this address is trusted (it would pair at once).
        using var page = new ClientWebSocket();
        page.Options.AddSubProtocol(WebSocketLineChannel.SubProtocol);
        page.Options.SetRequestHeader("Origin", "http://evil.example");
        page.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => page.ConnectAsync(uri, CancellationToken.None));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, page.HttpStatusCode);
        Assert.Equal(0, host.LineSockets);

        // The node's own origin is let in, as is a controller that sends none (WsLines).
        using var own = new ClientWebSocket();
        own.Options.AddSubProtocol(WebSocketLineChannel.SubProtocol);
        own.Options.SetRequestHeader("Origin", "http://127.0.0.1:" + host.Port);
        await own.ConnectAsync(uri, CancellationToken.None);
        Assert.Equal(WebSocketLineChannel.SubProtocol, own.SubProtocol);
        await own.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await using var controller = await WsLines.ConnectAsync(host.Port);
        Assert.Equal("OK @v1", await controller.AskAsync("@v1 BLACKOUT ON"));
        Assert.Equal("BLACKOUT", Assert.Single(endpoint.Lines).Line.Verb);
    }

    [Fact]
    public async Task OnATrustedAddressItIsPairedAtOnceAndTheJsonSocketIsUnchanged()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        await using var ws = await WsLines.ConnectAsync(host.Port);
        await ws.SendAsync("HELLO Companion:Booth");
        Assert.Equal("OK", await ws.ReadAsync());
        Assert.StartsWith("STATE {", await ws.ReadAsync(), StringComparison.Ordinal);
        Assert.Equal("OK @v1", await ws.AskAsync("@v1 BLACKOUT ON"));

        // Without the subprotocol /ws is the screens' JSON socket.
        using var json = new ClientWebSocket();
        await json.ConnectAsync(new Uri("ws://127.0.0.1:" + host.Port + "/ws"), CancellationToken.None);
        var buffer = new byte[64 * 1024];
        var r = await json.ReceiveAsync(buffer, CancellationToken.None);
        Assert.StartsWith("{\"type\":\"state\"", Encoding.UTF8.GetString(buffer, 0, r.Count), StringComparison.Ordinal);
        Assert.Null(json.SubProtocol);
        await json.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }
}

/// <summary>The desk is the operator, so its role is never a name alone: on the TCP wire "PlayMapDesk" is the desk only once it proves
/// the node's token on its own connection (trust of an address is not proof); a WebSocket never yields it.</summary>
public sealed class DeskIdentityTests
{
    [Fact]
    public async Task TheDeskNameWithoutProofIsAWireClientAndWithItTheDesk()
    {
        var endpoint = new FakeEndpoint();
        await using var server = new WireServer(TestSettings.Make(trustLoopback: true), endpoint, TestSettings.Wall(), new WireServerOptions { Port = 0 });
        server.Start();

        // Trusted by address and claiming the desk's name: paired, but no desk — and asked at once to prove the token.
        await using (var claimer = await LineClient.ConnectAsync(server.Port))
        {
            Assert.Equal("OK", await claimer.AskAsync("HELLO PlayMapDesk"));
            Assert.StartsWith("NONCE ", await claimer.ReplyAsync(), StringComparison.Ordinal);
            Assert.Equal("OK", await claimer.AskAsync("BLACKOUT ON"));
        }
        var claimed = endpoint.Lines.Single().Session;
        Assert.True(claimed.Paired);
        Assert.Equal("wire", claimed.Transport);

        // The same name with the token proved on this connection (the proof answers the nonce it was asked with): the desk.
        await using (var desk = await LineClient.ConnectAsync(server.Port))
        {
            Assert.Equal("OK", await desk.AskAsync("HELLO PlayMapDesk"));
            var nonce = (await desk.ReplyAsync())!;
            Assert.StartsWith("NONCE ", nonce, StringComparison.Ordinal);
            Assert.Equal("OK paired", await desk.AskAsync("AUTH-PROOF " + Pairing.Proof(TestSettings.Token, nonce[6..], WireSession.DeskName)));
            Assert.Equal("OK", await desk.AskAsync("BLACKOUT OFF"));
        }
        Assert.Equal("desk", endpoint.Lines.Last().Session.Transport);

        // Plain AUTH from this machine (an older desk's link) proves it too.
        await using (var older = await LineClient.ConnectAsync(server.Port))
        {
            Assert.Equal("OK", await older.AskAsync("HELLO PlayMapDesk"));
            Assert.StartsWith("NONCE ", await older.ReplyAsync(), StringComparison.Ordinal);
            Assert.Equal("OK paired", await older.AskAsync("AUTH " + TestSettings.Token));
            Assert.Equal("OK", await older.AskAsync("BLACKOUT ON"));
        }
        Assert.Equal(("BLACKOUT", "desk"), (endpoint.Lines.Last().Line.Verb, endpoint.Lines.Last().Session.Transport));
    }

    [Fact]
    public async Task TheDesksLinkProvesItselfAtOnceOnATrustedNode()
    {
        var endpoint = new FakeEndpoint();
        await using var server = new WireServer(TestSettings.Make(trustLoopback: true), endpoint, TestSettings.Wall(), new WireServerOptions { Port = 0 });
        server.Start();
        // The desk's own link, with a nonce wait far past this test's patience: it is the desk only if the node asks it to prove.
        await using var link = new PlayMap.Net.Links.WireClient(new PlayMap.Net.Links.WireClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            ClientName = WireSession.DeskName,
            Token = TestSettings.Token,
            NonceWait = TimeSpan.FromMinutes(10),
        }, TestSettings.Wall());
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PlayMap.Net.Links.PairState.Paired));
        Assert.True((await link.SendAsync("BLACKOUT ON", CancellationToken.None)).Ok);
        Assert.Equal("desk", endpoint.Lines.Last().Session.Transport);
    }

    [Fact]
    public async Task ACompanionNameIsACompanionAndTheRegistryHearsHelloAndGone()
    {
        var endpoint = new RegistryEndpoint(new FakeEndpoint());
        await using var server = new WireServer(TestSettings.Make(trustLoopback: true), endpoint, TestSettings.Wall(), new WireServerOptions { Port = 0 });
        server.Start();
        await using (var c = await LineClient.ConnectAsync(server.Port))
        {
            Assert.Equal("OK", await c.AskAsync("HELLO Companion:FOH"));
            Assert.True(await Eventually.TrueAsync(() => endpoint.Hellos.Count == 1));
        }
        var (session, channel) = endpoint.Hellos.Single();
        Assert.Equal("companion", session.Transport);
        Assert.Equal("tcp", channel);
        Assert.True(await Eventually.TrueAsync(() => endpoint.Gone.Contains(session.SessionId)));
    }

    [Fact]
    public async Task ANewTokenUnpairsAClientInTheTableAtOnce()
    {
        var endpoint = new RegistryEndpoint(new FakeEndpoint());
        await using var server = new WireServer(TestSettings.Make(trustLoopback: false), endpoint, TestSettings.Wall(), new WireServerOptions { Port = 0 });
        server.Start();
        await using var c = await LineClient.ConnectAsync(server.Port);
        var nonce = (await c.ReadAsync())!;
        Assert.StartsWith("NONCE ", nonce, StringComparison.Ordinal);
        Assert.Equal("OK", await c.AskAsync("HELLO Companion:FOH"));
        Assert.Equal("OK paired", await c.AskAsync("AUTH-PROOF " + Pairing.Proof(TestSettings.Token, nonce[6..], "Companion:FOH")));
        Assert.True(await Eventually.TrueAsync(() => endpoint.Hellos.Any(h => h.Item1.Paired)));

        // A new token unpairs the session at once (WireServer.UpdateSettings): the table hears it then, not at the session's next line.
        server.UpdateSettings(TestSettings.Make(trustLoopback: false, token: "another-token-for-this-node"));
        var (now, channel) = endpoint.Hellos.Last();
        Assert.False(now.Paired);
        Assert.Equal("companion", now.Transport);
        Assert.Equal("tcp", channel);
    }

    private sealed class RegistryEndpoint(FakeEndpoint inner) : IWireEndpoint, IClientRegistry
    {
        public System.Collections.Concurrent.ConcurrentQueue<(SessionInfo, string)> Hellos { get; } = new();

        public System.Collections.Concurrent.ConcurrentBag<string> Gone { get; } = [];

        public void Hello(SessionInfo session, string channel) => Hellos.Enqueue((session, channel));

        void IClientRegistry.Gone(string sessionId) => Gone.Add(sessionId);

        public ValueTask<CommandReply> HandleAsync(WireLine line, SessionInfo session, CancellationToken ct) => inner.HandleAsync(line, session, ct);

        public (string Json, long Revision) State() => inner.State();

        public string Context() => inner.Context();

        public (string Json, long Revision) ProjectJson() => inner.ProjectJson();

        public event Action<long>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public event Action<long>? ProjectChanged
        {
            add => inner.ProjectChanged += value;
            remove => inner.ProjectChanged -= value;
        }
    }
}

/// <summary>MCP asks Show Mode before it runs or queues a line: a refusal reaches the assistant in Show Mode's words and nothing runs
/// or waits for a person.</summary>
public sealed class McpShowModeTests
{
    private sealed class ArmedEndpoint(FakeEndpoint inner) : IWireEndpoint, IShowDecisions
    {
        public int Asked;

        public ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session)
        {
            Interlocked.Increment(ref Asked);
            Assert.Equal("mcp", session.Transport);
            return spec.Name == "BLACKOUT" ? new ShowDecision(ShowVerdict.Refuse, "the show is armed — BLACKOUT is the desk's") : new ShowDecision(ShowVerdict.Allow, null);
        }

        public ValueTask<CommandReply> HandleAsync(WireLine line, SessionInfo session, CancellationToken ct) => inner.HandleAsync(line, session, ct);

        public (string Json, long Revision) State() => inner.State();

        public string Context() => inner.Context();

        public (string Json, long Revision) ProjectJson() => inner.ProjectJson();

        public event Action<long>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public event Action<long>? ProjectChanged
        {
            add => inner.ProjectChanged += value;
            remove => inner.ProjectChanged -= value;
        }
    }

    [Fact]
    public async Task ALineShowModeRefusesIsNeitherRunNorQueued()
    {
        var inner = new FakeEndpoint();
        var endpoint = new ArmedEndpoint(inner);
        var asked = 0;
        var policy = new SwitchApprovalPolicy(() =>
        {
            Interlocked.Increment(ref asked);
            return true;
        });
        await using var host = new HttpHost(TestSettings.Make(trustLoopback: true), endpoint, TestSettings.Wall(), new HttpHostOptions { Port = 0, Approvals = policy });
        await host.StartAsync();
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://127.0.0.1:" + host.Port + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }), cancellationToken: CancellationToken.None);

        var refused = await client.CallToolAsync("run_command", new Dictionary<string, object?> { ["line"] = "BLACKOUT ON", ["reason"] = "dark now" });
        Assert.True(refused.IsError);
        Assert.Equal("the show is armed — BLACKOUT is the desk's", string.Concat(refused.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.False(inner.Blackout);
        Assert.Empty(inner.Lines);
        Assert.Equal(0, Volatile.Read(ref asked));

        var ran = await client.CallToolAsync("run_command", new Dictionary<string, object?> { ["line"] = "VIDEO SEEK 83", ["reason"] = "cue" });
        Assert.NotEqual(true, ran.IsError);
        Assert.Equal(2, Volatile.Read(ref endpoint.Asked));
        Assert.Equal("VIDEO", Assert.Single(inner.Lines).Line.Verb);
    }
}
