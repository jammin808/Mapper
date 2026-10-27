using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net.Commands;
using PlayMap.Net.Http;
using PlayMap.Net.Wire;

namespace PlayMap.Net.Tests;

/// <summary>The line wire as Patterns, Stagewatch's VT link and a Patterns "Lines" device meet it: the pairing
/// transcript, the clock exchange, receipts, the fences (unpaired verbs, wrong proofs, the per-address ceiling,
/// lines that never end), facts and a peer's own replies never answered, faults that are the line's, and pushes
/// that are throttled, latest-wins and never repeated. The node's own token arriving in clear from another machine — plain AUTH on
/// the wire, a token header on plain HTTP at the web's gate — gets the answer the door gives any such line or request, and is reported.
/// A Stagewatch session hears the node's STATE whole, one line within what its reader takes, on the wire's one clock.</summary>
public class WireServerTests
{
    private static readonly Regex Nonce = new("^NONCE [0-9a-f]{32}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static async Task<WireServer> StartAsync(IWireEndpoint endpoint, NodeSettings settings, WireServerOptions? options = null, ShowWallClock? wall = null)
    {
        var server = new WireServer(settings, endpoint, wall ?? TestSettings.Wall(), options ?? new WireServerOptions { PlainAuthOnLoopback = false });
        server.Start();
        await Task.Yield();
        return server;
    }

    [Fact]
    public async Task ThePatternsTranscriptReplaysExactly()
    {
        var endpoint = new FakeEndpoint();
        await using var server = await StartAsync(endpoint, TestSettings.Make(trustLoopback: false));
        await using var c = await LineClient.ConnectAsync(server.Port);

        var nonceLine = await c.ReadAsync();
        Assert.Matches(Nonce, nonceLine!);
        var nonce = nonceLine![6..];

        var pong = await c.AskAsync("@a1 PING 1760101234567");
        var p = pong!.Split(' ');
        Assert.Equal(["OK", "@a1", "PONG", "1760101234567"], p[..4]);
        Assert.True(long.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture) <= long.Parse(p[5], System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal("ERR @b2 " + WireWords.NotPaired, await c.AskAsync("@b2 BLACKOUT ON"));
        Assert.False(endpoint.Blackout);
        Assert.Empty(endpoint.Lines);

        Assert.Equal("OK", await c.AskAsync("HELLO Stagewatch"));
        var proof = Pairing.Proof(TestSettings.Token, nonce, "Stagewatch");
        Assert.Equal("OK paired", await c.AskAsync("AUTH-PROOF " + proof));
        Assert.Equal("OK @c3", await c.AskAsync("@c3 BLACKOUT ON"));
        Assert.True(endpoint.Blackout);
        Assert.True(endpoint.Lines.TryDequeue(out var ran));
        Assert.Equal("BLACKOUT", ran.Line.Verb);
        Assert.True(ran.Session.Paired);
        Assert.Equal("Stagewatch", ran.Session.ClientName);
        Assert.Equal("stagewatch", ran.Session.Transport);

        var fresh = await c.AskAsync("NONCE");
        Assert.Matches("^OK [0-9a-f]{32}$", fresh!);
        Assert.NotEqual(nonce, fresh![3..]);
        Assert.StartsWith("STATE {\"v\":1,\"seq\":", c.Pushes[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStagewatchStyleClientPingsReadsStateAndSeeksWithAReceipt()
    {
        var endpoint = new FakeEndpoint();
        await using var server = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        await using var c = await LineClient.ConnectAsync(server.Port);

        await c.SendAsync("HELLO Stagewatch");
        Assert.Equal("OK", await c.ReadAsync());
        var state = await c.ReadAsync();
        Assert.StartsWith("STATE {\"v\":1,\"seq\":1,\"id\":\"a1b2c3d4\"", state, StringComparison.Ordinal);
        Assert.Contains("\"positionMs\":62000", state, StringComparison.Ordinal);

        Assert.StartsWith("OK @hi PONG 1700000000000 ", await c.AskAsync("@hi PING 1700000000000"), StringComparison.Ordinal);
        Assert.Equal("OK @s7", await c.AskAsync("@s7 VIDEO SEEK 83"));
        var (line, session) = Assert.Single(endpoint.Lines);
        Assert.Equal("83", line.Word(2));
        Assert.True(session.IsLoopback);
        Assert.True(session.Paired);

        var context = await c.AskAsync("@ctx CONTEXT");
        Assert.StartsWith("OK @ctx {\"v\":1,\"node\":{\"t\":\"playmap\"", context, StringComparison.Ordinal);
        Assert.StartsWith("OK @q {\"v\":1,\"seq\":", await c.AskAsync("@q STATE"), StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root (PlayMap.sln) is not above the test");
    }

    /// <summary>A §14 row of the contract's copy (docs/SHOWBUS.md).</summary>
    private static long Section14(string name)
    {
        var row = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "SHOWBUS.md")).Split('\n').Select(l => l.TrimEnd('\r'))
            .Single(l => l.StartsWith(name + " ", StringComparison.Ordinal));
        return long.Parse(row[(name.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The most of a STATE line in which a reader that keeps only its head still finds the clip on air and the playlists
    /// (Stagewatch's playout link reads <c>video</c> and <c>playlists</c> from a line it keeps in part).</summary>
    private const int HeadBytes = 16 * 1024;

    /// <summary>The wire's half of the fence on what a Stagewatch session hears: the STATE a <c>HELLO Stagewatch</c> session is greeted
    /// and pushed is the node's STATE whole, one line, never cut, split or reordered by the wire, up to the most a reader takes
    /// (Show Bus 1.3 §13.14, §14 <c>link.read.min.bytes</c>) and past it as well; and every whole STATE the node's own tests wrote
    /// (tests/fixtures/state) is one such line well within it, with the clip on air and the playlists in its head. A real show's STATE,
    /// written by the engine, is measured in the Engine suite's ContextLinkTests.</summary>
    [Fact]
    public async Task AStagewatchSessionHearsTheWholeStateAsOneLineWithinTheReadersBound()
    {
        var bound = (int)Section14("link.read.min.bytes");
        Assert.Equal(256 * 1024, bound);
        var inner = new FakeEndpoint();
        // A STATE whose line is the bound to the byte (its seq moves with the node; its pad takes up the seq's digits, so its length does
        // not), then one a byte past it.
        var extra = 0;
        string Sized()
        {
            var head = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{{\"v\":1,\"seq\":{inner.Seq},\"id\":\"a1b2c3d4\",\"t\":\"playmap\",\"video\":{{\"file\":\"intro.mp4\"}},\"playlists\":[],\"pad\":\"");
            return head + new string('p', bound - "STATE ".Length - head.Length - 2 + Volatile.Read(ref extra)) + "\"}";
        }
        // A line a reader takes as STATE: JSON whose seq is the node's.
        void IsState(string line, long seq)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line["STATE ".Length..]);
            Assert.Equal(seq, doc.RootElement.GetProperty("seq").GetInt64());
        }
        var endpoint = new ShapedEndpoint(inner) { StateJson = Sized };
        await using (var server = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true)))
        {
            await using var c = await LineClient.ConnectAsync(server.Port);
            Assert.Equal("OK", await c.AskAsync("HELLO Stagewatch"));
            var greeting = await c.ReadAsync();
            Assert.Equal("STATE " + Sized(), greeting);
            Assert.Equal(bound, Encoding.UTF8.GetByteCount(greeting!));
            IsState(greeting!, inner.Seq);

            Volatile.Write(ref extra, 1);
            inner.Publish();
            var pushed = await c.ReadAsync();
            Assert.Equal("STATE " + Sized(), pushed);
            Assert.Equal(bound + 1, Encoding.UTF8.GetByteCount(pushed!));
            IsState(pushed!, 2);
        }

        // The node's own whole STATEs, as its engine tests wrote them, compact as the engine writes them.
        var dir = Path.Combine(RepoRoot(), "tests", "fixtures", "state");
        var whole = Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".cards.json", StringComparison.Ordinal))
            .Select(f => (Name: Path.GetFileName(f), Json: System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f))))
            .Where(f => f.Json is System.Text.Json.Nodes.JsonObject o && o["t"]?.GetValue<string>() == "playmap")
            .Select(f => (f.Name, Json: f.Json!.ToJsonString())).ToList();
        Assert.Contains(whole, f => f.Name == "main.json");
        foreach (var (name, json) in whole)
        {
            var node = new ShapedEndpoint(new FakeEndpoint()) { StateJson = () => json };
            await using var server = await StartAsync(node, TestSettings.Make(trustLoopback: true));
            await using var c = await LineClient.ConnectAsync(server.Port);
            Assert.Equal("OK", await c.AskAsync("HELLO Stagewatch"));
            var line = await c.ReadAsync();
            Assert.Equal("STATE " + json, line);
            var bytes = Encoding.UTF8.GetByteCount(line!);
            Assert.True(bytes <= bound / 4, $"{name}: a STATE line of {bytes} bytes, past a quarter of the reader's {bound}");
            Assert.InRange(line!.IndexOf("\"video\":", StringComparison.Ordinal), 0, HeadBytes);
            Assert.InRange(line.IndexOf("\"playlists\":", StringComparison.Ordinal), 0, HeadBytes);
        }
    }

    /// <summary>One clock: a <c>HELLO Stagewatch</c> session's PONG carries the wire's show clock — the one the node's STATE and its
    /// <c>AT</c> read (the host hands the wire and the engine one wall) — for both its read and its answer, never this machine's own clock
    /// beside it.</summary>
    [Fact]
    public async Task AStagewatchSessionsPongReadsTheWiresOneClock()
    {
        var mono = new ManualClock(1_000_000_000);
        var wall = new ShowWallClock(mono, 1_700_000_000_000);
        await using var server = await StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: true), wall: wall);
        await using var c = await LineClient.ConnectAsync(server.Port);
        Assert.Equal("OK", await c.AskAsync("HELLO Stagewatch"));
        Assert.StartsWith("STATE ", await c.ReadAsync(), StringComparison.Ordinal);
        Assert.Equal("OK @p1 PONG 1699999999000 1700000000000 1700000000000", await c.AskAsync("@p1 PING 1699999999000"));
        mono.AdvanceMs(1500);
        Assert.Equal("OK @p2 PONG 1700000001400 1700000001500 1700000001500", await c.AskAsync("@p2 PING 1700000001400"));
    }

    [Fact]
    public async Task AWrongProofGetsAFreshNonceAndTheRightProofForItPairs()
    {
        var endpoint = new FakeEndpoint();
        await using var server = await StartAsync(endpoint, TestSettings.Make());
        await using var c = await LineClient.ConnectAsync(server.Port);
        var first = (await c.ReadAsync())![6..];
        Assert.Equal("OK", await c.AskAsync("HELLO Desk"));

        Assert.Equal("ERR " + WireWords.WrongToken, await c.AskAsync("AUTH-PROOF " + Pairing.Proof("WRONG-TOKEN-0", first, "Desk")));
        var next = await c.ReplyAsync();
        Assert.Matches(Nonce, next!);
        var second = next![6..];
        Assert.NotEqual(first, second);

        // The first nonce answered one proof: a proof for it no longer pairs.
        Assert.Equal("ERR " + WireWords.WrongToken, await c.AskAsync("AUTH-PROOF " + Pairing.Proof(TestSettings.Token, first, "Desk")));
        var third = (await c.ReplyAsync())![6..];
        Assert.Equal("OK paired", await c.AskAsync("AUTH-PROOF " + Pairing.Proof(TestSettings.Token, third, "Desk")));
        Assert.Equal("OK @x", await c.AskAsync("@x BLACKOUT ON"));
        Assert.True(endpoint.Blackout);
    }

    [Fact]
    public async Task FiveWrongProofsCloseTheConnection()
    {
        var endpoint = new FakeEndpoint();
        await using var server = await StartAsync(endpoint, TestSettings.Make());
        await using var c = await LineClient.ConnectAsync(server.Port);
        Assert.Matches(Nonce, (await c.ReadAsync())!);
        for (var i = 1; i <= 4; i++)
        {
            Assert.Equal("ERR " + WireWords.WrongToken, await c.AskAsync("AUTH-PROOF " + new string('0', 64)));
            Assert.Matches(Nonce, (await c.ReplyAsync())!);
        }
        Assert.Equal("ERR " + WireWords.WrongToken + " — closed", await c.AskAsync("AUTH-PROOF " + new string('0', 64)));
        Assert.Null(await c.ReadAsync());
    }

    [Fact]
    public async Task PlainAuthIsRefusedFromAnotherMachineAndAnOpenNodeSaysSo()
    {
        await using (var server = await StartAsync(new FakeEndpoint(), TestSettings.Make()))
        {
            await using var c = await LineClient.ConnectAsync(server.Port);
            await c.ReadAsync();
            Assert.Equal("ERR @a " + WireWords.UseProof, await c.AskAsync("@a AUTH " + TestSettings.Token));
        }
        await using (var server = await StartAsync(new FakeEndpoint(), TestSettings.Make(), new WireServerOptions { PlainAuthOnLoopback = true }))
        {
            await using var c = await LineClient.ConnectAsync(server.Port);
            await c.ReadAsync();
            Assert.Equal("OK @a paired", await c.AskAsync("@a AUTH k7qm3xwdp9ra"));
        }
        await using (var server = await StartAsync(new FakeEndpoint(), TestSettings.Make(token: "")))
        {
            await using var c = await LineClient.ConnectAsync(server.Port);
            Assert.Equal("OK " + WireWords.Open, await c.AskAsync("AUTH-PROOF " + new string('a', 64)));
        }
    }

    [Fact]
    public async Task TheNodesTokenInClearFromAnotherMachineIsAnsweredAsBeforeAndReported()
    {
        var seen = new ConcurrentQueue<ClearSighting>();
        await using (var server = await StartAsync(new FakeEndpoint(), TestSettings.Make(), new WireServerOptions { PlainAuthOnLoopback = false, SeenInClear = seen.Enqueue }))
        {
            await using var c = await LineClient.ConnectAsync(server.Port);
            await c.ReadAsync();
            // A wrong token in clear is no secret of this node's: refused, not reported.
            Assert.Equal("ERR @a " + WireWords.UseProof, await c.AskAsync("@a AUTH AAAA-BBBB-CCCC"));
            Assert.Empty(seen);
            // The node's own (in any spelling the token takes) is refused as any plain AUTH from another machine is, and reported as seen
            // in clear.
            Assert.Equal("ERR @b " + WireWords.UseProof, await c.AskAsync("@b AUTH k7qm3xwdp9ra"));
            var wire = Assert.Single(seen);
            Assert.Equal(("127.0.0.1", ClearSighting.Wire, "plain AUTH on the line wire"), (wire.From, wire.Carrier, wire.How));
        }
        await using (var server = await StartAsync(new FakeEndpoint(), TestSettings.Make(), new WireServerOptions { PlainAuthOnLoopback = true, SeenInClear = seen.Enqueue }))
        {
            // On this machine plain AUTH is taken, and the token crossed no network.
            await using var c = await LineClient.ConnectAsync(server.Port);
            await c.ReadAsync();
            Assert.Equal("OK @a paired", await c.AskAsync("@a AUTH " + TestSettings.Token));
            Assert.Single(seen);
        }

        // The web's gate: the verdict is what it was; the node's token in a header on plain HTTP from another machine is reported.
        var gate = new TokenGate(seenInClear: seen.Enqueue);
        var settings = TestSettings.Make(trustLoopback: true, TestSettings.Token, "10.0.0.7");
        static DefaultHttpContext From(string address, string? token, bool https = false, string header = "X-PlayMap-Token")
        {
            var c = new DefaultHttpContext();
            c.Connection.RemoteIpAddress = IPAddress.Parse(address);
            c.Request.IsHttps = https;
            if (token is not null)
                c.Request.Headers[header] = header == "Authorization" ? "Bearer " + token : token;
            return c;
        }
        Assert.Equal(GateVerdict.WrongToken, gate.Check(From("10.0.0.9", "AAAA-BBBB-CCCC"), settings));
        Assert.Equal(GateVerdict.Token, gate.Check(From("10.0.0.9", TestSettings.Token, https: true), settings));
        Assert.Equal(GateVerdict.Open, gate.Check(From("127.0.0.1", TestSettings.Token), settings));
        Assert.Single(seen);
        Assert.Equal(GateVerdict.Token, gate.Check(From("10.0.0.9", TestSettings.Token), settings));
        Assert.Equal(GateVerdict.Open, gate.Check(From("10.0.0.7", TestSettings.Token, header: "Authorization"), settings));
        Assert.Equal(GateVerdict.NeedsHttps, gate.Check(From("10.0.0.8", TestSettings.Token), settings with { RequireHttps = true }));
        // A node that asks no token has none to leak.
        Assert.Equal(GateVerdict.Open, gate.Check(From("10.0.0.6", TestSettings.Token), settings with { Token = "" }));
        Assert.Equal(
            [("10.0.0.9", ClearSighting.Http), ("10.0.0.7", ClearSighting.Http), ("10.0.0.8", ClearSighting.Http)],
            seen.Skip(1).Select(s => (s.From, s.Carrier)));
        Assert.Equal("a token header on plain HTTP", seen.Last().How);
    }

    [Fact]
    public async Task FactLinesAndAPeersOwnRepliesAreNeverAnswered()
    {
        var endpoint = new FakeEndpoint();
        await using var server = await StartAsync(endpoint, TestSettings.Make(false, TestSettings.Token, "127.0.0.1"));
        await using var c = await LineClient.ConnectAsync(server.Port);
        foreach (var line in new[] { "BLACKOUT 1", "LIVE 0", "OK", "ERR you said what", "OK @a1 PONG 1 2 3", "ERROR nope", "DECK 3 12", "LOOK \"Keynote blue\"" })
            await c.SendAsync(line);
        Assert.StartsWith("OK @z PONG 5 ", await c.AskAsync("@z PING 5"), StringComparison.Ordinal);
        Assert.True(endpoint.FactBlackout);
        var verbs = endpoint.Lines.Select(l => l.Line.Verb).ToList();
        Assert.Equal(["BLACKOUT", "LIVE", "DECK", "LOOK"], verbs);
        Assert.All(endpoint.Lines, l => Assert.Equal("patterns-device", l.Session.Transport));
    }

    [Fact]
    public async Task ALineWithARequestIdIsAlwaysAnsweredEvenByAVerbThatAnswersNothing()
    {
        var endpoint = new FakeEndpoint();
        await using var server = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        await using var c = await LineClient.ConnectAsync(server.Port);
        // A cursor's stream is never answered; a cursor's line that carries a request id is, since its sender waits for that id. Replies
        // come in order, so the first reply heard is the asked line's.
        await c.SendAsync("CURSOR op_1 0.5 0.5");
        Assert.Equal("OK @c", await c.AskAsync("@c CURSOR op_1 0.5 0.5"));
        // A fact's shape with a request id is no fact: the endpoint is asked it as a line, and it is answered.
        Assert.Equal("OK @l", await c.AskAsync("@l LIVE 0"));
        Assert.Equal(("LIVE", "l"), (endpoint.Lines.Last().Line.Verb, endpoint.Lines.Last().Line.RequestId));
        Assert.Null(CommandRunner.WireLineFor(null, CommandReply.NoReply()));
        Assert.Equal("OK @x", CommandRunner.WireLineFor("x", CommandReply.NoReply()));
    }

    [Fact]
    public async Task AnUnpairedPeersFactIsNotTakenIn()
    {
        var endpoint = new FakeEndpoint();
        await using var server = await StartAsync(endpoint, TestSettings.Make());
        await using var c = await LineClient.ConnectAsync(server.Port);
        Assert.Matches(Nonce, (await c.ReadAsync())!);
        await c.SendAsync("BLACKOUT 1");
        Assert.StartsWith("OK @z PONG 5 ", await c.AskAsync("@z PING 5"), StringComparison.Ordinal);
        Assert.False(endpoint.FactBlackout);
        Assert.Empty(endpoint.Lines);
    }

    [Fact]
    public async Task ARefusedVerbSaysWhyAndAnAcceptedOneSaysItsStage()
    {
        var endpoint = new FakeEndpoint();
        await using var server = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        await using var c = await LineClient.ConnectAsync(server.Port);
        Assert.Equal("OK @a", await c.AskAsync("@a BLACKOUT ON"));
        Assert.Equal("ERR @b already blacked out", await c.AskAsync("@b BLACKOUT ON"));
        Assert.Equal("OK @s {\"stage\":\"accepted\",\"effect\":\"scan started\"}", await c.AskAsync("@s SCAN START cam1"));
        Assert.Equal("ERR @u unknown word 'FROBNICATE'", await c.AskAsync("@u FROBNICATE now"));
        Assert.Equal("OK @n pl_1", await c.AskAsync("@n PLAYLIST NEW Show"));
        Assert.StartsWith("ERR a request id is @", await c.AskAsync("@bad!id PING"), StringComparison.Ordinal);
        Assert.StartsWith("OK @v [{\"name\":\"HELLO\"", await c.AskAsync("@v VERBS"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFaultingEndpointRefusesThatLineAndTheConnectionSurvives()
    {
        var endpoint = new FakeEndpoint();
        await using var server = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        await using var c = await LineClient.ConnectAsync(server.Port);
        Assert.Equal("ERR @f the node faulted on this line (InvalidOperationException) — fault #1, logged", await c.AskAsync("@f BOOM"));
        Assert.Equal(1, server.Faults);
        Assert.StartsWith("OK @p PONG 9 ", await c.AskAsync("@p PING 9"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionVerbThatFaultsIsThatLinesFaultToo()
    {
        var endpoint = new FakeEndpoint { ThrowOnState = true };
        await using var server = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        await using var c = await LineClient.ConnectAsync(server.Port);
        await c.SendAsync("@h HELLO Stagewatch");
        Assert.Equal("OK @h", await c.ReadAsync());
        Assert.Equal("ERR @h the node faulted on this line (InvalidOperationException) — fault #1, logged", await c.ReadAsync());
        endpoint.ThrowOnState = false;
        Assert.StartsWith("OK @p PONG 3 ", await c.AskAsync("@p PING 3"), StringComparison.Ordinal);
    }

    /// <summary>The throttle's window runs on a clock the test moves once the window stands: the changes made inside it are one push,
    /// however slowly the test makes them, and a push that would say what the last one said is skipped.</summary>
    [Fact]
    public async Task PushesAreThrottledLatestWinsAndNeverRepeated()
    {
        var endpoint = new FakeEndpoint();
        var time = new ManualTime();
        var options = new WireServerOptions { PlainAuthOnLoopback = false, Time = time };
        await using var server = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true), options);
        await using var c = await LineClient.ConnectAsync(server.Port);
        Assert.Equal("OK", await c.AskAsync("HELLO Stagewatch"));
        Assert.Contains("\"seq\":1,", await c.ReadAsync(), StringComparison.Ordinal);

        for (var i = 0; i < 50; i++)
            endpoint.Publish();
        Assert.True(await Eventually.TrueAsync(() => time.Waiting >= 1), "no push window stands");
        time.Advance(options.PushThrottle);
        var states = new List<string> { await c.ReadAsync() ?? "" };
        while (await c.TryReadAsync(700) is { } line)
            states.Add(line);
        Assert.InRange(states.Count, 1, 2);
        Assert.Contains("\"seq\":51,", states[^1], StringComparison.Ordinal);

        endpoint.Touch();
        endpoint.Touch();
        Assert.True(await Eventually.TrueAsync(() => time.Waiting >= 1), "no push window stands for the touches");
        time.Advance(options.PushThrottle);
        Assert.Null(await c.TryReadAsync(600));
        Assert.True(await Eventually.TrueAsync(() => server.PushesSkipped >= 1), $"skipped {server.PushesSkipped}");
    }

    [Fact]
    public async Task WhileTheNodeAnimatesListenersHearStateEverySecondWithoutAnEvent()
    {
        var endpoint = new FakeEndpoint();
        await using var server = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        await using var c = await LineClient.ConnectAsync(server.Port);
        Assert.Equal("OK", await c.AskAsync("HELLO Stagewatch"));
        Assert.Contains("\"positionMs\":62000", await c.ReadAsync(), StringComparison.Ordinal);
        endpoint.IsAnimating = true;
        endpoint.MoveSilently(63000);
        var pushed = await c.ReadAsync(3000);
        Assert.Contains("\"positionMs\":63000", pushed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionsAddContextAndProjectPushesAndOffStopsThem()
    {
        var endpoint = new FakeEndpoint();
        await using var server = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        await using var c = await LineClient.ConnectAsync(server.Port);
        Assert.Equal("OK @p subscribed project", await c.AskAsync("@p SUBSCRIBE PROJECT"));
        Assert.Equal("PROJECT {\"revision\":1,\"project\":{\"name\":\"Show\"}}", await c.ReadAsync());
        endpoint.RenameProject("Tour");
        Assert.Equal("PROJECT {\"revision\":2,\"project\":{\"name\":\"Tour\"}}", await c.ReadAsync());
        Assert.Equal("OK @c subscribed context", await c.AskAsync("@c SUBSCRIBE CONTEXT"));
        Assert.StartsWith("CONTEXT {\"v\":1", await c.ReadAsync(), StringComparison.Ordinal);
        Assert.Equal("OK @o pushes off", await c.AskAsync("@o SUBSCRIBE OFF"));
        endpoint.RenameProject("Gone");
        endpoint.Publish();
        Assert.Null(await c.TryReadAsync(600));
        Assert.Equal("ERR @x " + WireWords.Subscribe, await c.AskAsync("@x SUBSCRIBE EVERYTHING"));
    }

    [Fact]
    public async Task TheSeventeenthConnectionFromOneAddressIsRefusedWithTheBusyWords()
    {
        await using var server = await StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: true));
        var open = new List<LineClient>();
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var c = await LineClient.ConnectAsync(server.Port);
                open.Add(c);
                Assert.StartsWith("OK PONG", await c.AskAsync("PING"), StringComparison.Ordinal);
            }
            await using var refused = await LineClient.ConnectAsync(server.Port);
            Assert.Equal("ERR busy — 16 connections already open from this address; close one first", await refused.ReadAsync());
            Assert.Null(await refused.ReadAsync());
            Assert.Equal(16, server.Connections);
        }
        finally
        {
            foreach (var c in open)
                await c.DisposeAsync();
        }
        Assert.True(await Eventually.TrueAsync(() => server.Connections == 0));
    }

    [Fact]
    public async Task ALineThatNeverEndsOrRunsPastTheLimitIsRefusedAndClosed()
    {
        var options = new WireServerOptions { Limits = WireLimits.Default with { LineSeconds = 0.4 } };
        await using var server = await StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: true), options);
        await using (var slow = await LineClient.ConnectAsync(server.Port))
        {
            await slow.SendRawAsync(Encoding.UTF8.GetBytes("PING 12"));
            Assert.Equal("ERR a line that did not end within 0.4 s — the wire's lines are commands, and this one was not; closed", await slow.ReadAsync());
            Assert.Null(await slow.ReadAsync());
        }
        await using var big = await LineClient.ConnectAsync(server.Port);
        await big.SendRawAsync(Encoding.UTF8.GetBytes(new string('A', 70 * 1024)));
        Assert.Equal("ERR a line past 64 KiB — the wire's lines are commands, and this one was not; closed", await big.ReadAsync());
    }

    [Fact]
    public async Task ABindThatIsNotAnAddressOfThisMachineKeepsTheWireClosed()
    {
        await using var a = new WireServer(TestSettings.Make() with { Bind = "stage-left" }, new FakeEndpoint(), TestSettings.Wall());
        var ex = Assert.Throws<BindRefusedException>(a.Start);
        Assert.Contains("not an IP address", ex.Message, StringComparison.Ordinal);
        await using var b = new WireServer(TestSettings.Make() with { Bind = "192.0.2.77" }, new FakeEndpoint(), TestSettings.Wall());
        Assert.Throws<BindRefusedException>(b.Start);
    }

    [Fact]
    public async Task ANewTokenUnpairsAConnectionThatProvedTheOldOne()
    {
        var endpoint = new FakeEndpoint();
        await using var server = await StartAsync(endpoint, TestSettings.Make());
        await using var c = await LineClient.ConnectAsync(server.Port);
        var nonce = (await c.ReadAsync())![6..];
        Assert.Equal("OK paired", await c.AskAsync("AUTH-PROOF " + Pairing.Proof(TestSettings.Token, nonce, "")));
        server.UpdateSettings(server.Settings with { Token = "AAAA-BBBB-CCCC" });
        Assert.Equal("ERR @x " + WireWords.NotPaired, await c.AskAsync("@x BLACKOUT ON"));
    }
}
