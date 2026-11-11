using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine;
using PlayMap.Engine.Show;
using PlayMap.Engine.Tests.Fleet;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Net.Links;
using PlayMap.Net.Wire;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests;

/// <summary>A Stagewatch's peer door played small: <c>/ws</c> sends a link a state frame when it comes and another for each message it
/// sends, so the link is heard and stays live; <c>/api/peers/context</c> answers a body that declares more than
/// <see cref="MaxContextBytes"/> 413 with Stagewatch's words before reading it, as Stagewatch's web server does, and takes any
/// other.</summary>
internal sealed class ContextStagewatch : IAsyncDisposable
{
    private readonly WebApplication _app;
    private long _max = 64 * 1024;
    private long _seq;

    private ContextStagewatch(WebApplication app) => _app = app;

    public int Port { get; private set; }

    /// <summary>The most a context post's body may declare before it is answered 413.</summary>
    public long MaxContextBytes
    {
        get => Interlocked.Read(ref _max);
        set => Interlocked.Exchange(ref _max, value);
    }

    /// <summary>The bodies taken, as posted.</summary>
    public ConcurrentQueue<string> Contexts { get; } = new();

    /// <summary>The lengths the posts answered 413 declared.</summary>
    public ConcurrentQueue<long> Oversize { get; } = new();

    public static async Task<ContextStagewatch> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var fake = new ContextStagewatch(app);
        app.UseWebSockets();
        app.Map("/ws", fake.WsAsync);
        app.MapPost("/api/peers/context", fake.ContextAsync);
        await app.StartAsync();
        fake.Port = new Uri(app.Urls.First()).Port;
        return fake;
    }

    private async Task ContextAsync(HttpContext c)
    {
        var max = MaxContextBytes;
        c.Response.ContentType = "application/json";
        if (c.Request.ContentLength is { } declared && declared > max)
        {
            Oversize.Enqueue(declared);
            c.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await c.Response.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"{{\"ok\":false,\"error\":\"a request body is at most {max} bytes; this one declares {declared}\"}}"), c.RequestAborted);
            return;
        }
        using var reader = new StreamReader(c.Request.Body);
        Contexts.Enqueue(await reader.ReadToEndAsync(c.RequestAborted));
        await c.Response.WriteAsync("{\"ok\":true}", c.RequestAborted);
    }

    private async Task WsAsync(HttpContext c)
    {
        if (!c.WebSockets.IsWebSocketRequest)
        {
            c.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var ws = await c.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[8192];
        try
        {
            await FrameAsync(ws, c.RequestAborted);
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, c.RequestAborted);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
                if (r.EndOfMessage)
                    await FrameAsync(ws, c.RequestAborted);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The link closed its end.
        }
    }

    private Task FrameAsync(WebSocket ws, CancellationToken ct) => ws.SendAsync(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
        $"{{\"type\":\"state\",\"v\":1,\"seq\":{Interlocked.Increment(ref _seq)},\"mode\":\"countdown\",\"state\":\"running\",\"seconds\":750,\"text\":\"12:30\",\"total\":1800,\"phase\":\"normal\",\"color\":\"#22C55E\",\"title\":\"Keynote\",\"message\":\"\"}}")),
        WebSocketMessageType.Text, true, ct);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>What a node gives its links. The context's links use only the verbs Patterns' Eye draws: a link of any other kind would be
/// dropped there silently. What reaches another node is said as Show Bus 1.3 §6's P2 and P3 say it: each link to Patterns or Stagewatch
/// is one row to <c>@&lt;the id its hello gave&gt;</c>, its item saying "not proved"; an output that draws Stagewatch's timer is fed by
/// <c>@&lt;its id&gt;/timer</c>; a playlist feeds the outputs it is on; and no row names a node no hello named, this node, an id two
/// machines say, or a thing the render does not draw. A context lists at most §14's <c>context.links</c>, the rows between nodes the
/// last it leaves out, and says how many it left out. A busy node's context, posted to Stagewatch, fits Stagewatch's 64 KB at the
/// poster with every link it keeps
/// still naming an item it keeps, and what a post did with it reaches STATE, the context and preflight. And a stress show's STATE, as a <c>HELLO Stagewatch</c> session hears it from the node's own wire, is
/// one line within what Stagewatch's reader takes, the clip on air and the playlists in its head, on the one clock its PONG and its
/// <c>AT</c> read.</summary>
public sealed class ContextLinkTests(ITestOutputHelper output)
{
    /// <summary>A network with both links live.</summary>
    private sealed class LinkedNetwork : INetworkView
    {
        public IReadOnlyList<PeerView> Peers() => [];
        public LinkView? Patterns() => new("live", "10.0.0.5:9697", true, "");
        public LinkView? Stagewatch() => new("live", "10.0.0.6:9923", true, "timer");
        public string? StagewatchTimerJson() => null;
        public SyncView Sync() => new("follower", "0badc0de", 0.2, 1, 0);
    }

    /// <summary>A network at its bounds: the hello table full (§14 <c>peers.max</c>, 128 peers, each name and headline at its most), both
    /// links live with words, and a Stagewatch frame whose title and message are as long as Stagewatch makes them, with its watermark.
    /// A test may have fewer peers heard, and the links name their far ends.</summary>
    private sealed class FullNetwork(string desk = "", string hall = "", int peers = 128) : INetworkView
    {
        public IReadOnlyList<PeerView> Peers() => [.. Enumerable.Range(0, peers).Select(i => new PeerView(i % 3 == 0 ? "main" : i % 3 == 1 ? "patterns" : "playmap",
            i.ToString("x8", CultureInfo.InvariantCulture), $"Peer {i:000} " + new string('n', 52), i % 4 == 0 ? "amber" : "green",
            $"Peer {i:000}: " + new string('h', 190), true))];

        public LinkView? Patterns() => new("live", "10.0.0.5:9697", true, "paired · pinned 5a7e 91c0 · following its blackout") { PeerId = desk };

        public LinkView? Stagewatch() => new("live", "10.0.0.6:9923", true,
            "Stagewatch's certificate pinned (5a7e 91c0) · showing timer · context trimmed for Stagewatch (left out: 20 recent)") { PeerId = hall };

        public string? StagewatchTimerJson() => new StagewatchReading(new StagewatchTimer
        {
            Seq = 9,
            State = "running",
            Seconds = 750,
            Text = "12:30",
            Phase = "warning",
            Color = "#F59E0B",
            Title = new string('T', 200),
            Message = new string('M', 500),
            Watermark = "StageWatch by Ben Rohleder",
        }, true, "12:30", "").ToStateJson();

        public SyncView Sync() => new("follower", "5a5a5a5a", 0.2, 1, 0);
    }

    /// <summary>A stress show: 64 outputs, 32 projectors each with its control device, 24 playlists, on the busy node's project.</summary>
    private static Project StressShow()
    {
        var p = EyeContextTests.Busy();
        var projectors = Enumerable.Range(1, 32).Select(i => p.Stage.Projectors[0] with
        {
            Id = "pj" + i.ToString("x8", CultureInfo.InvariantCulture),
            Name = $"Stage projector {i:00}",
        }).ToList();
        var outputs = Enumerable.Range(1, 64).Select(i => p.Outputs[2] with { Id = "op_" + i, Name = $"Output {i:00} — wall {i % 8}" }).ToList();
        for (var i = 0; i < projectors.Count; i++)
            outputs[i] = outputs[i] with { Mode = OutputMode.Projector, ProjectorId = projectors[i].Id };
        var devices = projectors.Select((pj, i) => new DeviceLink("dv" + (i + 1).ToString("x8", CultureInfo.InvariantCulture), $"Box {i + 1:00}", "pjlink",
            $"10.0.1.{i + 1}", 0, "", pj.Id, outputs[i].Id, 0, false)).ToList();
        var playlists = Enumerable.Range(0, 24).Select(i => i < p.Playlists.Length ? p.Playlists[i] : p.Playlists[0] with { Id = "pl_" + i, Name = $"Playlist {i:00}" })
            .ToList();
        return p with
        {
            Outputs = [.. outputs],
            Playlists = [.. playlists],
            Stage = p.Stage with { Projectors = [.. projectors], Devices = [.. devices] },
        };
    }

    private static async Task<EngineCore> StressEngineAsync(INetworkView? network = null, Func<Project, Project>? shape = null)
    {
        var health = new HealthTests.Outputs
        {
            Now = [.. Enumerable.Range(1, 64).Select(i => new OutputHealth("op_" + i, i != 7, i == 7 ? 0 : 60, i == 12 ? 3 : 0, 4.1, "DP-" + i))],
        };
        var services = HealthTests.Services(new ManualTime(), health, new HealthTests.FakeSampler()) with { Network = network ?? new FullNetwork() };
        var engine = new EngineCore(services, shape is null ? StressShow() : shape(StressShow()));
        foreach (var go in new[] { "PLAYLIST GO pl_show", "PLAYLIST GO pl_walk", "PLAYLIST GO pl_5" })
        {
            Assert.True(WireGrammar.TryParse(go, out var line, out _));
            Assert.True((await engine.HandleAsync(line, SessionInfo.Desk("test"), CancellationToken.None)).Ok, go);
        }
        return engine;
    }

    private static long Section14(string name)
    {
        var row = File.ReadAllText(Path.Combine(NodeExe.RepoRoot(), "docs", "SHOWBUS.md")).Split('\n').Select(l => l.TrimEnd('\r'))
            .Single(l => l.StartsWith(name + " ", StringComparison.Ordinal));
        return long.Parse(row[(name.Length + 1)..], CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task EveryLinkTheContextEmitsIsAnEyeVerb()
    {
        var p = Project.CreateDefault("Show");
        var projector = new Projector("pj0a0b0c01", "Left", Intrinsics.FromThrowRatio(1920, 1080, 1.5, 0, 0.5), LensDistortion.None,
            Pose.LookAt(new Vec3d(0, 1.5, -6), new Vec3d(0, 1.5, 0), Vec3d.UnitY), 12000, "#FF8800", false, [], SolveLocks.None, 0.8);
        p = p with
        {
            Stage = p.Stage with
            {
                Projectors = [projector],
                Devices = [new DeviceLink("dv0a0b0c01", "Left box", "pjlink", "10.0.0.21", 0, "", projector.Id, "op_1", 0, false)],
            },
            Outputs = p.Outputs.SetItem(0, p.Outputs[0] with { Mode = OutputMode.Projector, ProjectorId = projector.Id }),
        };
        var services = EngineServices.ForTests(new ManualClock(5_000_000_000)) with { Network = new LinkedNetwork() };
        await using var engine = new EngineCore(services, p);

        var context = ContextBuilder.Build(engine);

        Assert.Contains(context.Links, l => l.Kind == "drives");
        Assert.Contains(context.Links, l => l.From == "link:patterns");
        Assert.Contains(context.Links, l => l.From == "device:dv0a0b0c01");
        Assert.All(context.Links, l => Assert.True(ContextLinkKinds.IsEyeVerb(l.Kind), $"{l.From} → {l.To}: {l.Kind}"));
        Assert.Equal(11, ContextLinkKinds.All.Count);
        Assert.False(ContextLinkKinds.IsEyeVerb("Drives"));
        Assert.False(ContextLinkKinds.IsEyeVerb("plays"));
    }

    /// <summary>A node's journal: its last twenty rows each a whole 64 KB line, as a long line journalled whole makes one.</summary>
    private static void LongRows(EngineCore engine)
    {
        for (var i = 0; i < ContextBuilder.RecentRows; i++)
            engine.Journal.Add("line", $"row {i:00}: " + new string((char)('a' + i), 64 * 1024));
    }

    private static string Field(string element, string name)
    {
        using var doc = JsonDocument.Parse(element);
        return doc.RootElement.GetProperty(name).GetString()!;
    }

    private static Dictionary<string, List<string>> Arrays(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new[] { "items", "links", "recent", "commands" }
            .ToDictionary(a => a, a => doc.RootElement.GetProperty(a).EnumerateArray().Select(e => e.GetRawText()).ToList());
    }

    /// <summary>The busy node (its golden's show: 20 outputs, 199 commands) with twenty 64 KB journal rows: built, its context is over a
    /// megabyte and stays so; posted, it fits Stagewatch's bytes with every row kept, cut to the 200 characters Stagewatch's reader keeps
    /// of one, and every item, link and command as built.</summary>
    [Fact]
    public async Task ABusyNodesPostKeepsEveryRowCutToWhatStagewatchReadsAndEverythingElseAsBuilt()
    {
        var health = new HealthTests.Outputs
        {
            Now = [.. Enumerable.Range(1, 20).Select(i => new OutputHealth("op_" + i, i != 7, i == 7 ? 0 : 60, i == 12 ? 3 : 0, 4.1, "DP-" + i))],
        };
        await using var engine = new EngineCore(HealthTests.Services(new ManualTime(), health, new HealthTests.FakeSampler()), EyeContextTests.Busy());
        Assert.True(WireGrammar.TryParse("PLAYLIST GO pl_show", out var go, out _));
        Assert.True((await engine.HandleAsync(go, SessionInfo.Desk("test"), CancellationToken.None)).Ok);
        LongRows(engine);
        var built = engine.Context();
        Assert.True(Encoding.UTF8.GetByteCount(built) > 20 * 64 * 1024);

        var post = ContextFit.ForPost(built);
        Assert.NotNull(post.Body);
        Assert.InRange(post.Bytes, 1, (int)Section14("context.bytes"));
        Assert.Equal("cut short: 20 recent", post.What);
        var given = Arrays(built);
        var sent = Arrays(post.Body);
        Assert.Equal(20, sent["recent"].Count);
        Assert.All(sent["recent"], r => Assert.True(Field(r, "text") is { Length: ContextFit.RecentChars } t && t.EndsWith('…'), r));
        Assert.Equal(given["items"], sent["items"]);
        Assert.Equal(given["links"], sent["links"]);
        Assert.Equal(given["commands"], sent["commands"]);
        Assert.Equal(built, engine.Context());
    }

    /// <summary>The stress show with twenty 64 KB journal rows: its context, without them, is already past Stagewatch's bytes, so the post
    /// leaves out in §6's order — every recent row first, then links — says what it left out, and every link it keeps names an item it
    /// keeps.</summary>
    [Fact]
    public async Task AStressShowsPostLeavesOutInSection6sOrderAndEveryLinkItKeepsNamesAnItemItKeeps()
    {
        await using var engine = await StressEngineAsync();
        var bare = Encoding.UTF8.GetByteCount(engine.Context());
        LongRows(engine);
        var built = engine.Context();
        var post = ContextFit.ForPost(built);
        output.WriteLine($"a stress show's context: {bare} bytes built without its journal, {Encoding.UTF8.GetByteCount(built)} with twenty 64 KB rows; {post.Bytes} posted ({post.What})");
        Assert.NotNull(post.Body);
        Assert.InRange(post.Bytes, 1, (int)Section14("context.bytes"));
        Assert.True(bare > ContextFit.MaxBytes, $"the stress show's own context is {bare} bytes");
        Assert.Equal(20, post.LeftOut.Recent);
        Assert.True(post.LeftOut.Links > 0);

        var sent = Arrays(post.Body);
        Assert.Empty(sent["recent"]);
        var ids = sent["items"].Select(i => Field(i, "id")).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(ContextFit.TrimmedId, ids);
        foreach (var link in sent["links"])
        {
            foreach (var end in new[] { Field(link, "from"), Field(link, "to") })
                Assert.True(end.StartsWith('@') || ids.Contains(end), $"a kept link names {end}, an item left out");
        }
    }

    /// <summary>What a post did with this node's context reaches every reader of the link through the link's own words, with no word of the
    /// network host's: Stagewatch's 413, then a post fitted to its 64 KB, each said in STATE's <c>links.stagewatch.detail</c>, in the
    /// context's <c>link:stagewatch</c> item and in preflight's links row, and gone from them when the next post does otherwise.</summary>
    [Fact]
    public async Task WhatAPostDidWithTheContextReachesStateTheContextAndPreflight()
    {
        // The node's clock is the test's: the link's silence never grows, so it stays live however long the posts take (one each 5 s).
        var clock = new ManualClock(1_000_000_000_000);
        await using var sw = await ContextStagewatch.StartAsync();
        sw.MaxContextBytes = 1024;
        var address = "127.0.0.1:" + sw.Port.ToString(CultureInfo.InvariantCulture);
        await using var rig = await HostRig.StartAsync("Stage left", mono: clock,
            settings: s => s with { StagewatchEnabled = true, StagewatchHost = address, StagewatchKey = "swk_context0123456789" });
        string Detail() => rig.State().GetProperty("links").GetProperty("stagewatch").TryGetProperty("detail", out var d) ? d.GetString()! : "";
        string Item() => ContextBuilder.Build(rig.Engine).Items.Single(i => i.Id == "link:stagewatch").State;
        async Task<string> Row() => (await new LinksCheck().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None))
            .Single(r => r.Id == "links:stagewatch").Note;

        // A Stagewatch that takes 1 KB answers the first post 413: its words reach every reader.
        Assert.True(await Eventually.TrueAsync(() => !sw.Oversize.IsEmpty && Detail().Contains("(413)", StringComparison.Ordinal), 20_000), rig.LogTail());
        var tooLarge = $"Stagewatch at {address} refused this node's context as too large (413) — a request body is at most 1024 bytes; this one declares ";
        Assert.Contains(tooLarge, Detail(), StringComparison.Ordinal);
        Assert.Contains(tooLarge, Item(), StringComparison.Ordinal);
        Assert.Contains(tooLarge, await Row(), StringComparison.Ordinal);
        Assert.Empty(sw.Contexts);
        Assert.Equal("live", rig.State().GetProperty("links").GetProperty("stagewatch").GetProperty("state").GetString());

        // Twenty 64 KB rows in its journal and a Stagewatch that takes 64 KB: the next post is fitted and taken, and that is what every
        // reader says now; the 413 is gone from them.
        LongRows(rig.Engine);
        sw.MaxContextBytes = ContextFit.MaxBytes;
        const string fitted = "context fitted to Stagewatch's 64 KB: ";
        Assert.True(await Eventually.TrueAsync(() => !sw.Contexts.IsEmpty && Detail().Contains(fitted, StringComparison.Ordinal), 20_000), rig.LogTail());
        Assert.InRange(Encoding.UTF8.GetByteCount(sw.Contexts.First()), 1, ContextFit.MaxBytes);
        foreach (var said in new[] { Detail(), Item(), await Row() })
        {
            Assert.Contains(fitted, said, StringComparison.Ordinal);
            Assert.DoesNotContain("(413)", said, StringComparison.Ordinal);
        }
    }

    /// <summary>The fence on what a Stagewatch session hears (G26), measured on a stress show at the hello table's bound, over the node's
    /// own wire. The engine and the wire read one wall, as the host gives them: STATE's <c>at</c> and the PONG's two stamps are one
    /// instant on a clock that does not move, and an <c>AT</c> a sender works out from that PONG is judged on it (59 s back is now;
    /// 60 s and a millisecond back is refused).</summary>
    [Fact]
    public async Task AStressShowsStateReachesAStagewatchSessionWithinTheReadersBoundOnOneClock()
    {
        await using var engine = await StressEngineAsync();
        var settings = engine.Services.Settings with { Bind = "127.0.0.1", TrustLoopback = true };
        await using var wire = new WireServer(settings, engine, engine.Services.Wall, new WireServerOptions { Port = 0, PlainAuthOnLoopback = false });
        wire.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, wire.Port, stop.Token);
        var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        async Task<string> Next(string startsWith)
        {
            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                if (line.StartsWith(startsWith, StringComparison.Ordinal))
                    return line;
            }
            throw new IOException("the wire closed before " + startsWith);
        }
        async Task<string> Ask(string line, string id)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), stop.Token);
            return await Next(id.Length > 0 ? "OK " + id : "OK");
        }
        async Task<string> AskEither(string line, string id)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), stop.Token);
            while (await reader.ReadLineAsync(stop.Token) is { } answer)
            {
                if (answer.StartsWith("OK " + id, StringComparison.Ordinal) || answer.StartsWith("ERR " + id, StringComparison.Ordinal))
                    return answer;
            }
            throw new IOException("the wire closed before " + id);
        }

        Assert.Equal("OK", await Ask("HELLO Stagewatch", ""));
        var greeting = await Next("STATE ");
        var bound = Section14("link.read.min.bytes");
        var bytes = Encoding.UTF8.GetByteCount(greeting);
        output.WriteLine($"a stress show's STATE line for a HELLO Stagewatch session: {bytes} bytes of the reader's {bound}");
        Assert.True(bytes <= bound, $"a stress show's STATE line is {bytes} bytes, over the {bound} Stagewatch's reader takes: a video-shaped STATE for its session is due");
        Assert.InRange(greeting.IndexOf("\"video\":", StringComparison.Ordinal), 0, 16 * 1024);
        Assert.InRange(greeting.IndexOf("\"playlists\":", StringComparison.Ordinal), 0, 16 * 1024);

        // It is the show at its bounds, whole.
        using var state = JsonDocument.Parse(greeting["STATE ".Length..]);
        var root = state.RootElement;
        Assert.Equal((64, 24, 32, 128), (root.GetProperty("outputs").GetArrayLength(), root.GetProperty("playlists").GetArrayLength(),
            root.GetProperty("stage").GetProperty("projectors").GetArrayLength(), root.GetProperty("peers").GetArrayLength()));
        Assert.Equal(new string('M', 500), root.GetProperty("links").GetProperty("stagewatch").GetProperty("timer").GetProperty("message").GetString());

        // One clock.
        var at = root.GetProperty("at").GetInt64();
        Assert.Equal(engine.Services.Wall.UnixMs, at);
        var t1 = at - 250;
        Assert.Equal($"OK @p1 PONG {t1} {at} {at}", await Ask(string.Create(CultureInfo.InvariantCulture, $"@p1 PING {t1}"), "@p1"));
        Assert.StartsWith("OK @a1", await AskEither(string.Create(CultureInfo.InvariantCulture, $"@a1 PLAYLIST GO pl_7 AT {at - 59_000}"), "@a1"), StringComparison.Ordinal);
        Assert.Equal("ERR @a2 AT is a Unix ms within the next day (or +ms)",
            await AskEither(string.Create(CultureInfo.InvariantCulture, $"@a2 PLAYLIST GO pl_8 AT {at - 60_001}"), "@a2"));
    }

    // ---- §6 P2 and P3: the rows with an end on another node, and the playlists on the outputs. ----

    private const string DeskId = "9f8e7d6c";
    private const string HallId = "3f9c2a1b7d0e";
    private const string Timer = "@" + HallId + "/timer";

    /// <summary>A network a test sets: the peers heard and the two links as the host gives them.</summary>
    private sealed class Net : INetworkView
    {
        public List<PeerView> Heard { get; } = [];
        public LinkView? Desk { get; set; }
        public LinkView? Hall { get; set; }
        public IReadOnlyList<PeerView> Peers() => Heard;
        public LinkView? Patterns() => Desk;
        public LinkView? Stagewatch() => Hall;
        public string? StagewatchTimerJson() => null;
        public SyncView Sync() => new("solo", null, 0, 0, 0);
    }

    private static LinkView DeskLink(string id = DeskId, string state = "live", bool paired = true) =>
        new(state, "10.0.0.5:9697", paired, "") { PeerId = id };

    private static LinkView HallLink(string id = HallId, string state = "live", bool paired = true, string detail = "showing timer") =>
        new(state, "10.0.0.6:9924", paired, detail) { PeerId = id };

    /// <summary>The context of a show on a node with that network, after the lines ran.</summary>
    private static async Task<ShowContext> ContextOf(Project project, Net net, params string[] lines)
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)) with { Network = net }, project);
        foreach (var text in lines)
        {
            Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
            var reply = await engine.HandleAsync(line, SessionInfo.Desk("test"), CancellationToken.None);
            Assert.True(reply.Ok, $"{text} → {reply.Payload}");
        }
        return ContextBuilder.Build(engine);
    }

    private static List<(string From, string To, string Kind, Light Light)> Rows(ShowContext context) =>
        [.. context.Links.Select(l => (l.From, l.To, l.Kind, l.Light))];

    /// <summary>The rows with an end on another node.</summary>
    private static List<(string From, string To, string Kind, Light Light)> Claims(ShowContext context) =>
        [.. Rows(context).Where(r => r.From.StartsWith('@') || r.To.StartsWith('@'))];

    /// <summary>The rows that feed an output, as "from → output id".</summary>
    private static List<string> Feeds(ShowContext context) =>
        [.. Rows(context).Where(r => r.Kind == "feeds" && r.To.StartsWith("output:", StringComparison.Ordinal)).Select(r => $"{r.From} → {r.To["output:".Length..]}")];

    private static string State(ShowContext context, string id) => context.Items.Single(i => i.Id == id).State;

    private static Output Screen(string id, OutputMode mode, SourceRef source, string? projector = null) =>
        Project.CreateDefault("x").Outputs[0] with { Id = id, Name = id, Mode = mode, Source = source, ProjectorId = projector };

    private static Projector Beam(string id) => new(id, id, Intrinsics.FromThrowRatio(1920, 1080, 1.5, 0, 0.5), LensDistortion.None,
        Pose.LookAt(new Vec3d(0, 1.5, -6), new Vec3d(0, 1.5, 0), Vec3d.UnitY), 12000, "#FF8800", false, [], SolveLocks.None, 0.8);

    private static Surface Wall(string id, SourceRef source) =>
        new(id, id, "ms_wall", Transform3.Identity, [], SurfaceContent.Empty with { Source = source }, 1, [], true, false, "#ffffff", []);

    /// <summary>The mesh the tests' surfaces lie on: a stage surface is drawn only on a mesh the project holds.</summary>
    private static readonly MeshAsset WallMesh = new("ms_wall", "Wall", MeshOrigin.Primitive, "", "", "plane", ParamSet.Empty, 0, 0);

    private static Layer Tile(string id, SourceRef source) => new(id, id, source, new RectD(0, 0, 400, 200), 1, LayerBlend.Normal, true);

    private static PlaylistItem Entry(string id, SourceRef source) => PlaylistItem.For(id, source, id) with { DurationNs = 10_000_000_000 };

    private static readonly SourceRef TimerSource = SourceRef.Timer("stagewatch");

    /// <summary>A show of one source output on a base project that has a canvas <c>cv_timer</c> with the timer on a layer.</summary>
    private static Project Show(params Output[] outputs)
    {
        var p = Project.CreateDefault("Rows");
        return p with
        {
            Outputs = [.. outputs],
            Canvases = p.Canvases.Add(new Canvas("cv_timer", "Timer canvas", 1920, 1080, "#000000", [Tile("ly_timer", TimerSource)])),
        };
    }

    /// <summary>P2: each link this node holds is one row from its item to the node its hello named, of kind follows (it reads the desk's
    /// state, and Stagewatch's frames), in the link's own light, ahead of every other row; its item says "not proved", since no link of
    /// this node's speaks LINK yet; and the row from the Patterns link to the air it blacks out stays.</summary>
    [Fact]
    public async Task EachLinkIsOneRowToTheNodeItsHelloNamedAndItsItemSaysNotProved()
    {
        var net = new Net { Desk = DeskLink(), Hall = HallLink() };
        var context = await ContextOf(Project.CreateDefault("Show"), net);

        Assert.Equal([("link:patterns", "@" + DeskId, "follows", Light.Green), ("link:stagewatch", "@" + HallId, "follows", Light.Green)], Claims(context));
        Assert.Equal(Claims(context), Rows(context).Take(2));
        Assert.Equal("live · not proved", State(context, "link:patterns"));
        Assert.Equal("live · not proved, showing timer", State(context, "link:stagewatch"));
        Assert.Contains(Rows(context), r => r is { From: "link:patterns", Kind: "controls" } && !r.To.StartsWith('@'));
        // No item is another node's thing (P1), and every end that is not one of this node's items is §6's link end, within its sizes.
        var ids = context.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(ids, id => id.StartsWith('@'));
        foreach (var end in context.Links.SelectMany(l => new[] { l.From, l.To }).Where(e => !ids.Contains(e)))
        {
            Assert.Matches("^@[A-Za-z0-9_-]{6,40}(/.{1,60})?$", end);
            Assert.InRange(end.Length, 1, (int)Section14("context.link.end.chars"));
        }
        Assert.Equal((6L, 40L, 60L), (Section14("hello.id.min"), Section14("hello.id.max"), Section14("context.item.id.chars")));
        // As sent: §6's row, read back by this tree's own reader.
        var json = context.ToJson();
        Assert.Contains($"{{\"from\":\"link:patterns\",\"to\":\"@{DeskId}\",\"kind\":\"follows\",\"light\":\"green\"}}", json, StringComparison.Ordinal);
        Assert.Contains(ShowContext.Parse(json)!.Links, l => l is { From: "link:stagewatch", To: "@" + HallId, Kind: "follows" });

        // The row is in its link's light, whatever the link's state: amber while it is not paired or stale, red once it is down or held.
        foreach (var (state, paired, light) in new[] { ("live", false, Light.Amber), ("stale", true, Light.Amber), ("down", false, Light.Red), ("held", false, Light.Red) })
        {
            var other = await ContextOf(Project.CreateDefault("Show"), new Net { Desk = DeskLink(state: state, paired: paired), Hall = HallLink(state: state, paired: paired) });
            Assert.Equal([("link:patterns", "@" + DeskId, "follows", light), ("link:stagewatch", "@" + HallId, "follows", light)], Claims(other));
            Assert.All(other.Items.Where(i => i.Kind == "link"), i => Assert.Equal(light, i.Light));
            Assert.All(other.Items.Where(i => i.Kind == "link"), i => Assert.Contains(" · not proved", i.State, StringComparison.Ordinal));
        }
    }

    /// <summary>A link names no node, and its item's words are as they were, while no hello named its far end, while the id it has is
    /// no §2 id, is this node's own, or is one two machines say (§2's mark): no reader could place such an end.</summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("0badc0de", false)]         // this node's own id
    [InlineData(DeskId, true)]              // an id in conflict
    [InlineData("9f8e7", false)]            // under hello.id.min
    [InlineData("9f8e7d6c/timer", false)]
    [InlineData("9f 8e7d6c", false)]
    [InlineData("@9f8e7d6c", false)]
    [InlineData("9f8e7d6c9f8e7d6c9f8e7d6c9f8e7d6c9f8e7d6c9", false)]   // over hello.id.max
    public async Task ALinkWhoseFarEndNoReaderCouldPlaceNamesNoNode(string id, bool conflict)
    {
        var net = new Net { Desk = DeskLink(id), Hall = HallLink(id) };
        if (conflict)
            net.Heard.Add(new PeerView("patterns", id, "FOH", "amber", "two machines say they are " + id, true) { Conflict = true });
        var context = await ContextOf(Show(Screen("op_1", OutputMode.Source, TimerSource)), net);

        Assert.Empty(Claims(context));
        Assert.Equal("live", State(context, "link:patterns"));
        Assert.Equal("live, showing timer", State(context, "link:stagewatch"));
        // The same id with no mark on it is named.
        if (conflict)
        {
            net.Heard.Clear();
            net.Heard.Add(new PeerView("patterns", id, "FOH", "green", "Programme: VT1", true));
            Assert.Equal(3, Claims(await ContextOf(Show(Screen("op_1", OutputMode.Source, TimerSource)), net)).Count);
        }
    }

    /// <summary>P3: every output that draws Stagewatch's timer is fed by Stagewatch's own timer item, the row this node's claim in its
    /// Stagewatch link's light — a source output, a canvas output through a layer, a projector output through a surface. An output that
    /// draws none has no row, and no row names an output the context does not list.</summary>
    [Fact]
    public async Task EveryOutputThatDrawsTheTimerIsFedByStagewatchsTimer()
    {
        var net = new Net { Hall = HallLink() };
        var show = Show(
            Screen("op_src", OutputMode.Source, TimerSource),
            Screen("op_canvas", OutputMode.Canvas, SourceRef.Canvas("cv_timer")),
            Screen("op_stage", OutputMode.Projector, SourceRef.None, "pj_1"),
            Screen("op_card", OutputMode.Canvas, SourceRef.Canvas("cv_main")),
            Screen("op_off", OutputMode.Source, TimerSource) with { Enabled = false },
            Screen("op_virtual", OutputMode.Source, TimerSource) with { Kind = OutputKind.Virtual });
        show = show with { Stage = show.Stage with { Projectors = [Beam("pj_1")], Meshes = [WallMesh], Surfaces = [Wall("sf_wall", TimerSource)] } };
        var context = await ContextOf(show, net);

        Assert.Equal([$"{Timer} → op_src", $"{Timer} → op_canvas", $"{Timer} → op_stage"], Feeds(context));
        Assert.Equal([("link:stagewatch", "@" + HallId, "follows", Light.Green), (Timer, "output:op_src", "feeds", Light.Green),
            (Timer, "output:op_canvas", "feeds", Light.Green), (Timer, "output:op_stage", "feeds", Light.Green)], Claims(context));
        Assert.Equal(Claims(context), Rows(context).Take(4));
        var ids = context.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(Claims(context), r => Assert.True(ids.Contains(r.From) || ids.Contains(r.To), $"{r.From} → {r.To}"));
        Assert.DoesNotContain("output:op_off", ids);

        // The time on the output is only as good as the link: the row is amber while the link is not paired, red once it is down.
        Assert.All((await ContextOf(show, new Net { Hall = HallLink(paired: false) })).Links.Where(l => l.From == Timer), l => Assert.Equal(Light.Amber, l.Light));
        Assert.All((await ContextOf(show, new Net { Hall = HallLink(state: "down", paired: false) })).Links.Where(l => l.From == Timer), l => Assert.Equal(Light.Red, l.Light));
        Assert.Equal(3, (await ContextOf(show, new Net { Hall = HallLink(state: "down", paired: false) })).Links.Count(l => l.From == Timer));
        // No Stagewatch named, or none followed: the timer is drawn, and no row says whose.
        Assert.Empty(Claims(await ContextOf(show, new Net { Hall = HallLink(id: "") })));
        Assert.Empty(Claims(await ContextOf(show, new Net())));
        // The Patterns desk's id never stands for Stagewatch's.
        Assert.Equal([("link:patterns", "@" + DeskId, "follows", Light.Green)], Claims(await ContextOf(show, new Net { Desk = DeskLink() })));
    }

    /// <summary>Only what the render draws is claimed: a hidden or transparent layer, a canvas with no size, a hidden or transparent
    /// surface, a surface on a mesh the project does not hold, a pixel-mapped surface on another projector, a projector output whose
    /// projector is gone, a canvas output whose source is no canvas and a source output set to none draw no timer, so none is fed by
    /// it.</summary>
    [Fact]
    public async Task WhatTheRenderDoesNotDrawIsNotClaimed()
    {
        var net = new Net { Hall = HallLink() };
        async Task<List<string>> TimerOn(Project show) => Feeds(await ContextOf(show, net));
        Project WithLayer(Func<Layer, Layer> layer)
        {
            var p = Show(Screen("op_1", OutputMode.Canvas, SourceRef.Canvas("cv_timer")));
            return p with { Canvases = p.Canvases.Update("cv_timer", c => c with { Layers = [layer(c.Layers[0])] }) };
        }
        Project WithSurface(Func<Surface, Surface> surface, string projector = "pj_1", bool mesh = true)
        {
            var p = Show(Screen("op_1", OutputMode.Projector, SourceRef.None, projector));
            return p with
            {
                Stage = p.Stage with { Projectors = [Beam("pj_1"), Beam("pj_2")], Meshes = mesh ? [WallMesh] : [], Surfaces = [surface(Wall("sf_wall", TimerSource))] },
            };
        }
        var quad = new PixelQuad("pj_2", new Vec2d(0, 0), new Vec2d(1, 0), new Vec2d(1, 1), new Vec2d(0, 1), null);

        Assert.Equal([$"{Timer} → op_1"], await TimerOn(WithLayer(l => l)));
        Assert.Empty(await TimerOn(WithLayer(l => l with { Visible = false })));
        Assert.Empty(await TimerOn(WithLayer(l => l with { Opacity = 0 })));
        Assert.Empty(await TimerOn(WithLayer(l => l with { Rect = new RectD(0, 0, 0, 0) })));
        var sized = WithLayer(l => l);
        Assert.Empty(await TimerOn(sized with { Canvases = sized.Canvases.Update("cv_timer", c => c with { Width = 0 }) }));
        Assert.Empty(await TimerOn(sized with { Canvases = sized.Canvases.Update("cv_timer", c => c with { Height = 0 }) }));

        Assert.Equal([$"{Timer} → op_1"], await TimerOn(WithSurface(s => s)));
        Assert.Empty(await TimerOn(WithSurface(s => s with { Visible = false })));
        Assert.Empty(await TimerOn(WithSurface(s => s with { Opacity = 0 })));
        Assert.Empty(await TimerOn(WithSurface(s => s with { Pixel = quad })));
        Assert.Equal([$"{Timer} → op_1"], await TimerOn(WithSurface(s => s with { Pixel = quad }, projector: "pj_2")));
        Assert.Empty(await TimerOn(WithSurface(s => s, projector: "pj_gone")));
        // A stage surface is drawn on its mesh: one whose mesh the project does not hold puts nothing on the wall, by this
        // projector or any other; a pixel-mapped one is drawn as its quad, with or without a mesh, on its own projector alone.
        Assert.Empty(await TimerOn(WithSurface(s => s, mesh: false)));
        Assert.Empty(await TimerOn(WithSurface(s => s with { MeshId = "ms_gone" })));
        Assert.Equal([$"{Timer} → op_1"], await TimerOn(WithSurface(s => s with { Pixel = quad }, projector: "pj_2", mesh: false)));
        Assert.Empty(await TimerOn(WithSurface(s => s with { Pixel = quad }, mesh: false)));

        // A canvas output draws its canvas by its id alone, a source output its source: the timer as a canvas output's source is no
        // canvas, and a timer left on a projector output's source is not what a projector output draws.
        Assert.Empty(await TimerOn(Show(Screen("op_1", OutputMode.Canvas, TimerSource))));
        Assert.Empty(await TimerOn(Show(Screen("op_1", OutputMode.Source, SourceRef.None))));
        var left = Show(Screen("op_1", OutputMode.Projector, TimerSource, "pj_1"));
        Assert.Empty(await TimerOn(left with { Stage = left.Stage with { Projectors = [Beam("pj_1")] } }));
    }

    /// <summary>Which playlist is on which output: one row from the playlist to each output that shows it — as its source, on a canvas
    /// layer, on a stage surface — in the playlist's own light, grey while it is idle. Past a playlist only its item on the air is
    /// followed: a canvas item that carries the timer feeds the output while it plays (and while the item before it mixes into it),
    /// a playlist an item plays is on the output while that item plays, and a timer that is a playlist's own item draws nothing.</summary>
    [Fact]
    public async Task APlaylistFeedsTheOutputsItIsOnAndOnlyItsItemOnTheAirIsFollowedPastIt()
    {
        var net = new Net { Hall = HallLink() };
        var card = SourceRef.Generator("gn_testcard");
        var show = Show(
            Screen("op_src", OutputMode.Source, SourceRef.Playlist("pl_show")),
            Screen("op_canvas", OutputMode.Canvas, SourceRef.Canvas("cv_wall")),
            Screen("op_stage", OutputMode.Projector, SourceRef.None, "pj_1"),
            Screen("op_card", OutputMode.Canvas, SourceRef.Canvas("cv_main")));
        show = show with
        {
            Canvases = show.Canvases.Add(new Canvas("cv_wall", "Wall", 1920, 1080, "#000000", [Tile("ly_a", SourceRef.Playlist("pl_walk")), Tile("ly_b", SourceRef.Playlist("pl_show"))])),
            Stage = show.Stage with { Projectors = [Beam("pj_1")], Meshes = [WallMesh], Surfaces = [Wall("sf_wall", SourceRef.Playlist("pl_walk"))] },
            Playlists =
            [
                new Playlist("pl_show", "Show",
                [
                    Entry("it_card", card), Entry("it_timer", SourceRef.Canvas("cv_timer")) with { Transition = TransitionKind.Fade, TransitionNs = 1_000_000_000 },
                    Entry("it_own", TimerSource), Entry("it_list", SourceRef.Playlist("pl_inner")),
                ], false, null),
                new Playlist("pl_walk", "Walk-in", [Entry("it_w", card)], false, null),
                new Playlist("pl_inner", "Inner", [Entry("it_i", SourceRef.Canvas("cv_timer"))], false, null),
                new Playlist("pl_spare", "Spare", [Entry("it_s", card)], false, null),
            ],
        };
        string[] routed = ["playlist:pl_show → op_src", "playlist:pl_show → op_canvas", "playlist:pl_walk → op_canvas", "playlist:pl_walk → op_stage"];

        // Idle: every playlist is on its outputs, grey, and nothing past one is followed.
        var idle = await ContextOf(show, net);
        Assert.Equal(routed.Order(), Feeds(idle).Order());
        Assert.All(Rows(idle).Where(r => r.From.StartsWith("playlist:", StringComparison.Ordinal) && r.Kind == "feeds"), r => Assert.Equal(Light.Grey, r.Light));
        // Playing its first item, a generator: the rows are green, and no timer is on the air.
        var first = await ContextOf(show, net, "PLAYLIST GO pl_show");
        Assert.Equal(routed.Order(), Feeds(first).Order());
        Assert.All(Rows(first).Where(r => r.From == "playlist:pl_show"), r => Assert.Equal(Light.Green, r.Light));
        Assert.All(Rows(first).Where(r => r.From == "playlist:pl_walk"), r => Assert.Equal(Light.Grey, r.Light));
        // A GO into the fading canvas item: both pictures are on the air, so the timer feeds every output the playlist is on.
        var mixing = await ContextOf(show, net, "PLAYLIST GO pl_show", "PLAYLIST GO pl_show");
        Assert.Equal(routed.Append($"{Timer} → op_src").Append($"{Timer} → op_canvas").Order(), Feeds(mixing).Order());
        // On the canvas item itself (a cut to it).
        var onCanvas = await ContextOf(show, net, "PLAYLIST GO pl_show it_timer");
        Assert.Equal(new[] { $"{Timer} → op_src", $"{Timer} → op_canvas" }.Order(), Feeds(onCanvas).Where(f => f.StartsWith('@')).Order());
        // The timer as the playlist's own item: a playlist draws only its items' media, live inputs and drawn pictures.
        var own = await ContextOf(show, net, "PLAYLIST GO pl_show it_own");
        Assert.DoesNotContain(Feeds(own), f => f.StartsWith('@'));
        Assert.Equal(routed.Order(), Feeds(own).Order());
        // An item that plays another playlist: that playlist is on the outputs too, and what it plays is followed once it plays.
        var nested = await ContextOf(show, net, "PLAYLIST GO pl_show it_list");
        Assert.Equal(routed.Append("playlist:pl_inner → op_src").Append("playlist:pl_inner → op_canvas").Order(), Feeds(nested).Order());
        Assert.DoesNotContain(Feeds(nested), f => f.StartsWith('@'));
        var both = await ContextOf(show, net, "PLAYLIST GO pl_show it_list", "PLAYLIST GO pl_inner");
        Assert.Equal(new[] { $"{Timer} → op_src", $"{Timer} → op_canvas" }.Order(), Feeds(both).Where(f => f.StartsWith('@')).Order());
        // A playlist on no output has no row, and every row's ends are items the context lists.
        foreach (var context in new[] { idle, first, mixing, nested, both })
        {
            Assert.DoesNotContain(Rows(context), r => r.From == "playlist:pl_spare");
            var ids = context.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            Assert.All(Rows(context).Where(r => r.Kind == "feeds" && !r.From.StartsWith('@')), r => Assert.True(ids.Contains(r.From) && ids.Contains(r.To), $"{r.From} → {r.To}"));
        }
    }

    /// <summary>The walk stops where the render does: canvases nest four deep and playlists eight, a canvas inside itself and a playlist
    /// that plays itself are black there, and a canvas is read once, where the output first reaches it, as the render composes it
    /// once a frame.</summary>
    [Fact]
    public async Task TheWalkOfWhatAnOutputDrawsStopsWhereTheRenderDoes()
    {
        var resolver = Assembly.Load("PlayMap.Render").GetType("PlayMap.Render.Engine.SourceResolver", throwOnError: true)!;
        int Const(string name) => (int)resolver.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        Assert.Equal((Const("MaxCanvasDepth"), Const("MaxPlaylistDepth")), (ContextBuilder.CanvasDepth, ContextBuilder.PlaylistDepth));

        var net = new Net { Hall = HallLink() };
        // A chain of canvases, each a layer of the one before, the timer on the last: drawn at four deep, black at five.
        Project Chain(int depth)
        {
            var p = Show(Screen("op_1", OutputMode.Canvas, SourceRef.Canvas("cv_1")));
            var chain = Enumerable.Range(1, depth).Select(i => new Canvas("cv_" + i, "Canvas " + i, 1920, 1080, "#000000",
                [Tile("ly_" + i, i == depth ? TimerSource : SourceRef.Canvas("cv_" + (i + 1)))]));
            return p with { Canvases = [.. p.Canvases, .. chain] };
        }
        Assert.Equal([$"{Timer} → op_1"], Feeds(await ContextOf(Chain(ContextBuilder.CanvasDepth), net)));
        Assert.Empty(Feeds(await ContextOf(Chain(ContextBuilder.CanvasDepth + 1), net)));

        // A canvas that holds itself beside the timer, and sixteen layers each the same canvas of sixteen layers, three deep: read
        // once each, in no time a person would notice.
        var self = Show(Screen("op_1", OutputMode.Canvas, SourceRef.Canvas("cv_self")));
        self = self with { Canvases = self.Canvases.Add(new Canvas("cv_self", "Self", 1920, 1080, "#000000", [Tile("ly_s", SourceRef.Canvas("cv_self")), Tile("ly_t", TimerSource)])) };
        Assert.Equal([$"{Timer} → op_1"], Feeds(await ContextOf(self, net)));
        var wide = Show(Screen("op_1", OutputMode.Canvas, SourceRef.Canvas("cv_w1")));
        Canvas Wide(int i, SourceRef inner) => new("cv_w" + i, "Wide " + i, 1920, 1080, "#000000", [.. Enumerable.Range(0, 16).Select(k => Tile($"ly_w{i}_{k}", inner))]);
        wide = wide with { Canvases = [.. wide.Canvases, Wide(1, SourceRef.Canvas("cv_w2")), Wide(2, SourceRef.Canvas("cv_w3")), Wide(3, SourceRef.Canvas("cv_timer"))] };
        Assert.Equal([$"{Timer} → op_1"], Feeds(await ContextOf(wide, net)));

        // A canvas is composed once a frame, where it is first reached. Reached four deep first, its own layer's canvas is too deep
        // to draw, and the same picture — black where the timer would be — is what the nearer layer shows: no timer is on the output.
        // Reached near first, the timer is drawn in it, and the deeper layer shows that picture.
        Project Twice(params string[] layers)
        {
            var p = Show(Screen("op_1", OutputMode.Canvas, SourceRef.Canvas("cv_top")));
            return p with
            {
                Canvases =
                [
                    .. p.Canvases,
                    new Canvas("cv_top", "Top", 1920, 1080, "#000000", [.. layers.Select(l => Tile("ly_" + l, SourceRef.Canvas(l)))]),
                    new Canvas("cv_d1", "D1", 1920, 1080, "#000000", [Tile("ly_d1", SourceRef.Canvas("cv_d2"))]),
                    new Canvas("cv_d2", "D2", 1920, 1080, "#000000", [Tile("ly_d2", SourceRef.Canvas("cv_box"))]),
                    new Canvas("cv_box", "Box", 1920, 1080, "#000000", [Tile("ly_box", SourceRef.Canvas("cv_timer"))]),
                ],
            };
        }
        Assert.Empty(Feeds(await ContextOf(Twice("cv_d1", "cv_box"), net)));
        Assert.Equal([$"{Timer} → op_1"], Feeds(await ContextOf(Twice("cv_box", "cv_d1"), net)));
        // A canvas reached too deep is black there and is not composed: reached again within the depth, it is, with its timer.
        var refused = Twice("cv_d1", "cv_deep");
        refused = refused with { Canvases = refused.Canvases.Update("cv_box", c => c with { Layers = [Tile("ly_box", SourceRef.Canvas("cv_deep"))] })
            .Add(new Canvas("cv_deep", "Deep", 1920, 1080, "#000000", [Tile("ly_deep", SourceRef.Canvas("cv_timer"))])) };
        Assert.Equal([$"{Timer} → op_1"], Feeds(await ContextOf(refused, net)));

        // Playlists that play playlists: eight deep the last one's canvas is drawn, so its timer is claimed and all eight are on the
        // output; nine deep the ninth is black, so it has no row and its timer none.
        Project Lists(int depth)
        {
            var p = Show(Screen("op_1", OutputMode.Source, SourceRef.Playlist("pl_1")));
            return p with
            {
                Playlists = [.. Enumerable.Range(1, depth).Select(i => new Playlist("pl_" + i, "List " + i,
                    [Entry("it_" + i, i == depth ? SourceRef.Canvas("cv_timer") : SourceRef.Playlist("pl_" + (i + 1)))], false, null))],
            };
        }
        string[] Go(int depth) => [.. Enumerable.Range(1, depth).Select(i => "PLAYLIST GO pl_" + i)];
        string[] On(int depth) => [.. Enumerable.Range(1, depth).Select(i => $"playlist:pl_{i} → op_1")];
        Assert.Equal(On(ContextBuilder.PlaylistDepth).Append($"{Timer} → op_1").Order(),
            Feeds(await ContextOf(Lists(ContextBuilder.PlaylistDepth), net, Go(ContextBuilder.PlaylistDepth))).Order());
        Assert.Equal(On(ContextBuilder.PlaylistDepth).Order(),
            Feeds(await ContextOf(Lists(ContextBuilder.PlaylistDepth + 1), net, Go(ContextBuilder.PlaylistDepth + 1))).Order());

        // Two playlists that play each other (a project no verb makes): each is on the output once, and the walk ends.
        var loop = Show(Screen("op_1", OutputMode.Source, SourceRef.Playlist("pl_a")));
        loop = loop with
        {
            Playlists =
            [
                new Playlist("pl_a", "A", [Entry("it_a", SourceRef.Playlist("pl_b"))], false, null),
                new Playlist("pl_b", "B", [Entry("it_b", SourceRef.Playlist("pl_a"))], false, null),
            ],
        };
        Assert.Equal(["playlist:pl_a → op_1", "playlist:pl_b → op_1"], Feeds(await ContextOf(loop, net, "PLAYLIST GO pl_a", "PLAYLIST GO pl_b")));
    }

    /// <summary>§6's size for links: a context lists at most §14's <c>context.links</c>. Past it the last grey rows are left out
    /// first, then the last of the others, as §6 trims; every row with an end on another node is kept, leading the list; and one grey
    /// item says how many were left out. A context at the bound is whole and carries no such item.</summary>
    [Fact]
    public async Task AContextListsAtMostSection14sLinksTheRowsBetweenNodesKeptAndWhatWasLeftOutSaid()
    {
        Assert.Equal(ContextBuilder.MaxLinks, Section14("context.links"));
        var net = new Net { Desk = DeskLink(), Hall = HallLink() };
        Playlist List(int i) => new("pl_" + i, "List " + i, [Entry("it_" + i, SourceRef.Generator("gn_testcard"))], false, null);
        var lists = Enumerable.Range(1, 20).Select(List).ToList();
        var spare = Enumerable.Range(21, 40).Select(List).ToList();
        // Each wall output shows one canvas of twenty playlists and the timer: twenty playlist rows and a timer row an output. The
        // last output shows a canvas of some of the other playlists alone: a row each.
        Project Walls(int outputs, int fill)
        {
            var p = Show([.. Enumerable.Range(1, outputs).Select(i => Screen("op_" + i, OutputMode.Canvas, SourceRef.Canvas("cv_wall"))),
                Screen("op_fill", OutputMode.Canvas, SourceRef.Canvas("cv_fill"))]);
            return p with
            {
                Canvases = p.Canvases
                    .Add(new Canvas("cv_wall", "Wall", 1920, 1080, "#000000", [.. lists.Select(l => Tile("ly_" + l.Id, SourceRef.Playlist(l.Id))), Tile("ly_t", TimerSource)]))
                    .Add(new Canvas("cv_fill", "Fill", 1920, 1080, "#000000", [.. spare.Take(fill).Select(l => Tile("lf_" + l.Id, SourceRef.Playlist(l.Id)))])),
                Playlists = [.. lists, .. spare],
            };
        }
        static string? LeftOut(ShowContext context) => context.Items.SingleOrDefault(i => i.Id == ContextBuilder.LinksLeftOutId)?.State;
        static List<(string From, string To, string Kind, Light Light)> OnWalls(ShowContext context) =>
            [.. Rows(context).Where(r => r.From.StartsWith("playlist:", StringComparison.Ordinal) && r.Kind == "feeds")];

        // Under the bound, at it and one past it: seventeen walls leave room, which the fill takes row by row.
        var under = await ContextOf(Walls(17, 0), net);
        var room = ContextBuilder.MaxLinks - under.Links.Count;
        Assert.InRange(room, 1, spare.Count - 1);
        Assert.Null(LeftOut(under));
        var at = await ContextOf(Walls(17, room), net);
        Assert.Equal(ContextBuilder.MaxLinks, at.Links.Count);
        Assert.Null(LeftOut(at));
        var past = await ContextOf(Walls(17, room + 1), net);
        Assert.Equal(ContextBuilder.MaxLinks, past.Links.Count);
        Assert.Equal($"left out: 1 link (a context lists at most {ContextBuilder.MaxLinks})", LeftOut(past));
        Assert.Equal(Rows(at), Rows(past));
        Assert.Equal(("health", "Context", Light.Grey), past.Items.Where(i => i.Id == ContextBuilder.LinksLeftOutId).Select(i => (i.Kind, i.Label, i.Light)).Single());

        // Well past it, every playlist idle: 420 grey playlist rows on the walls. The grey ones go, from the end; the 21 timer rows
        // and the two links' rows lead the list, whole.
        var show = Walls(21, 0);
        string[] claims = ["link:patterns", "link:stagewatch", .. Enumerable.Repeat(Timer, 21)];
        var idle = await ContextOf(show, net);
        Assert.Equal(ContextBuilder.MaxLinks, idle.Links.Count);
        Assert.Equal(claims, Claims(idle).Select(r => r.From));
        Assert.Equal(Claims(idle), Rows(idle).Take(claims.Length));
        var kept = OnWalls(idle);
        var every = Enumerable.Range(1, 21).SelectMany(o => lists.Select(l => ("playlist:" + l.Id, "output:op_" + o))).ToList();
        Assert.Equal(every.Take(kept.Count), kept.Select(r => (r.From, r.To)));
        Assert.Equal($"left out: {every.Count - kept.Count} links (a context lists at most {ContextBuilder.MaxLinks})", LeftOut(idle));
        Assert.InRange(kept.Count, 1, every.Count - 1);
        // One playlist playing: its 21 green rows are kept, the last output's among them, and grey rows go in their place.
        var one = await ContextOf(show, net, "PLAYLIST GO pl_20");
        Assert.Equal(ContextBuilder.MaxLinks, one.Links.Count);
        Assert.Equal(21, OnWalls(one).Count(r => r is { From: "playlist:pl_20", Light: Light.Green }));
        Assert.Equal(claims, Claims(one).Select(r => r.From));
        Assert.Equal(LeftOut(idle), LeftOut(one));
        // Every playlist playing: no row is grey, so the last rows go, and the rows between nodes are still whole and first.
        var all = await ContextOf(show, net, [.. lists.Concat(spare).Select(l => "PLAYLIST GO " + l.Id)]);
        Assert.Equal(ContextBuilder.MaxLinks, all.Links.Count);
        Assert.Equal(Claims(all), Rows(all).Take(claims.Length));
        Assert.Equal(claims, Claims(all).Select(r => r.From));
        Assert.All(OnWalls(all), r => Assert.Equal(Light.Green, r.Light));
        Assert.Equal(every.Take(OnWalls(all).Count), OnWalls(all).Select(r => (r.From, r.To)));
        Assert.Equal(LeftOut(idle), LeftOut(all));
        // As sent, the context is within §6's size and every link it keeps names an item it keeps or another node.
        var ids = all.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(all.Links.SelectMany(l => new[] { l.From, l.To }), end => Assert.True(end.StartsWith('@') || ids.Contains(end), end));
        Assert.InRange(ShowContext.Parse(all.ToJson())!.Links.Count, claims.Length, ContextBuilder.MaxLinks);
    }

    /// <summary>A stress show whose links name their far ends and whose outputs draw the timer and show playlists, with twenty 64 KB
    /// journal rows, is over Stagewatch's bytes as built, and its post fits them. The rows between nodes lead the links and a post
    /// is trimmed from the end, grey rows first, so they are the last links a post leaves out: with the hello table full the post
    /// has no room for any link and leaves out commands besides; with fewer peers heard it leaves out some links and every row
    /// between nodes is in it, first, byte for byte.</summary>
    [Theory]
    [InlineData(128, false)]
    [InlineData(8, true)]
    public async Task AStressShowsFittedPostLeavesTheRowsBetweenNodesUntilLast(int peers, bool kept)
    {
        // The second half of the stress show's outputs: sixteen draw the timer, eight show a playlist that plays and eight an idle one.
        static Project Drawing(Project p) => p with
        {
            Outputs = [.. p.Outputs.Select((o, i) => i < 32 ? o
                : o with { Mode = OutputMode.Source, Source = i < 48 ? TimerSource : SourceRef.Playlist(i < 56 ? "pl_show" : "pl_9") })],
        };
        await using var engine = await StressEngineAsync(new FullNetwork(DeskId, HallId, peers), Drawing);
        LongRows(engine);
        var built = engine.Context();
        var given = Arrays(built)["links"];
        static bool Between(string link) => Field(link, "from").StartsWith('@') || Field(link, "to").StartsWith('@');
        var between = given.Where(Between).ToList();
        Assert.Equal(18, between.Count);
        Assert.Equal(between, given.Take(18));
        Assert.Equal(16, between.Count(l => Field(l, "from") == Timer && Field(l, "light") == "green"));
        Assert.Contains(given, l => Field(l, "from") == "playlist:pl_show" && Field(l, "light") == "green");
        Assert.Contains(given, l => Field(l, "from") == "playlist:pl_9" && Field(l, "light") == "grey");
        Assert.True(Encoding.UTF8.GetByteCount(built) > ContextFit.MaxBytes);

        var post = ContextFit.ForPost(built);
        output.WriteLine($"a stress show that draws the timer, {peers} peers heard: {given.Count} links built, {post.Bytes} bytes posted ({post.What})");
        Assert.NotNull(post.Body);
        Assert.InRange(post.Bytes, 1, (int)Section14("context.bytes"));
        var sent = Arrays(post.Body)["links"];
        Assert.True(post.LeftOut.Links > 0);
        Assert.Equal(given.Count - post.LeftOut.Links, sent.Count);
        // A row between nodes is left out only once every other link is.
        Assert.True(sent.Where(Between).SequenceEqual(between) || sent.TrueForAll(Between), post.What);
        if (kept)
        {
            // Links were left out, green ones among them, and every row between nodes is in the post, first.
            Assert.Equal(0, post.LeftOut.Commands);
            Assert.DoesNotContain(sent, l => Field(l, "light") == "grey");
            Assert.True(sent.Count(l => Field(l, "light") == "green") < given.Count(l => Field(l, "light") == "green"));
            Assert.Equal(between, sent.Take(18));
        }
        else
        {
            Assert.Empty(sent);
            Assert.True(post.LeftOut.Commands > 0);
        }
    }

    /// <summary>The busy node's show sets canvas outputs to a playlist's id, which is no canvas, and its projector outputs see no
    /// surface: they draw black, so its context — the golden Patterns' and Stagewatch's own tests read — claims no row for them.</summary>
    [Fact]
    public async Task TheBusyNodesContextClaimsNothingItsOutputsDoNotDraw()
    {
        var context = await ContextOf(EyeContextTests.Busy(), new Net { Desk = DeskLink(), Hall = HallLink() }, "PLAYLIST GO pl_show");
        Assert.Empty(Feeds(context));
        Assert.Equal(2, Claims(context).Count);
    }

    /// <summary>End to end on a node: the id is the one a hello gave from the link's own address and door. A Stagewatch followed at a
    /// typed address names no node until a hello is heard from there; then its link's row and the timer's row carry that id, and its
    /// item says "not proved". Two hellos that fit name none; a hello of another kind, or a Patterns hello on another door, is not the
    /// node the link dials.</summary>
    [Fact]
    public async Task TheIdALinkNamesIsTheOneAHelloGaveFromItsAddressAndDoor()
    {
        var clock = new ManualClock(1_000_000_000_000);
        await using var sw = await ContextStagewatch.StartAsync();
        const string deskToken = "AAAA-BBBB-CCCC";
        await using var desk = new FakePatternsDesk(deskToken);
        var address = "127.0.0.1:" + sw.Port.ToString(CultureInfo.InvariantCulture);
        await using var rig = await HostRig.StartAsync("Stage left", mono: clock,
            settings: s => s with { StagewatchEnabled = true, StagewatchHost = address, StagewatchKey = "swk_context0123456789" });
        await using var c = await rig.WireAsync();
        Assert.StartsWith("OK @l", await c.AskAsync($"@l LINK PATTERNS 127.0.0.1:{desk.Port} {deskToken}"), StringComparison.Ordinal);
        Assert.StartsWith("OK @o", await c.AskAsync("@o OUTPUT SOURCE op_1 timer:stagewatch"), StringComparison.Ordinal);
        JsonElement Link(string name) => rig.State().GetProperty("links").GetProperty(name);
        Assert.True(await Eventually.TrueAsync(() => Link("stagewatch").GetProperty("state").GetString() == "live" && Link("patterns").GetProperty("state").GetString() == "live"
            && Link("patterns").GetProperty("paired").GetBoolean(), 20_000), rig.LogTail());
        List<(string From, string To, string Kind, Light Light)> Now() => Claims(ContextBuilder.Build(rig.Engine));
        string Words(string id) => State(ContextBuilder.Build(rig.Engine), id);
        void Hear(string kind, string id, string name, string door, int port, string from = "127.0.0.1", string host = "FOH-PC") => rig.Network.Hello!.Peers.Hear(
            new ShowBusHello(kind, 1, 3, id, name, host, new Dictionary<string, int> { [door] = port }, ["context"], 0, id[..8], null), from);

        // No hello: both links are live and neither names a node; nor does a hello of another kind from the link's address and door,
        // or a Stagewatch's from another machine.
        Assert.Empty(Now());
        Hear("agent", "a1a2a3a4a5b6", "Slides", "http", sw.Port);
        Hear(ShowBusConstants.KindPlayMap, "b1b2b3b4b5c6", "Stage right", "wire", desk.Port);
        Hear(ShowBusConstants.KindStagewatch, "c1c2c3c4c5d6", "Hall C", "http", sw.Port, from: "10.9.9.9", host: "HALL-C");
        Assert.Empty(Now());
        Assert.DoesNotContain("not proved", Words("link:stagewatch") + Words("link:patterns"), StringComparison.Ordinal);

        // A Stagewatch heard from the link's address, its door the link's port.
        Hear(ShowBusConstants.KindStagewatch, HallId, "Hall A", "http", sw.Port);
        Assert.Equal([("link:stagewatch", "@" + HallId, "follows"), (Timer, "output:op_1", "feeds")], Now().Select(r => (r.From, r.To, r.Kind)));
        var heard = ContextBuilder.Build(rig.Engine);
        Assert.StartsWith("live · not proved", State(heard, "link:stagewatch"), StringComparison.Ordinal);
        Assert.All(Claims(heard), r => Assert.Equal(heard.Items.Single(i => i.Id == "link:stagewatch").Light, r.Light));
        // What the wire answers CONTEXT with carries the rows.
        Assert.Contains($"{{\"from\":\"{Timer}\",\"to\":\"output:op_1\",\"kind\":\"feeds\",", await c.AskAsync("@c CONTEXT"), StringComparison.Ordinal);

        // What Stagewatch takes of this node's context carries them.
        var timerRow = $"{{\"from\":\"{Timer}\",\"to\":\"output:op_1\",\"kind\":\"feeds\",";
        Assert.True(await Eventually.TrueAsync(() => sw.Contexts.Any(b => b.Contains(timerRow, StringComparison.Ordinal)
            && b.Contains($"\"to\":\"@{HallId}\",\"kind\":\"follows\"", StringComparison.Ordinal)), 20_000), rig.LogTail());

        // A link to an address is named only by a hello from that address: a hello from another machine that says the address as its
        // host name is not the node the link dials, and does not take the name away from the one that is.
        Hear(ShowBusConstants.KindStagewatch, "d1d2d3d4d5e6", "Hall D", "http", sw.Port, from: "10.9.9.9", host: "127.0.0.1");
        Hear(ShowBusConstants.KindPatterns, "e1e2e3e4e5f6", "Patterns C", "wire", desk.Port, from: "10.9.9.8", host: "127.0.0.1");
        Assert.Equal([("link:stagewatch", "@" + HallId, "follows"), (Timer, "output:op_1", "feeds")], Now().Select(r => (r.From, r.To, r.Kind)));
        // A Patterns hello on another door is not the desk this link dials; one from the link's address on the link's door is.
        Hear(ShowBusConstants.KindPatterns, "5e6f7a8b9c", "Patterns B", "wire", desk.Port == 1 ? 2 : 1);
        Assert.DoesNotContain(Now(), r => r.From == "link:patterns");
        Hear(ShowBusConstants.KindPatterns, DeskId, "Patterns FOH", "wire", desk.Port);
        Assert.Contains(("link:patterns", "@" + DeskId, "follows"), Now().Select(r => (r.From, r.To, r.Kind)));
        Assert.EndsWith(" · not proved", Words("link:patterns"), StringComparison.Ordinal);
        // The link's view moved, so the node's facts did: STATE still shows the link by its state, host, pairing and words alone.
        Assert.Equal(["state", "host", "paired", "detail", "clip"], Link("patterns").EnumerateObject().Select(p => p.Name).Take(5));
        Assert.False(Link("patterns").TryGetProperty("peer", out _) || Link("stagewatch").TryGetProperty("peer", out _));

        // A second Stagewatch heard from the same address and door: which one the link reaches is not known, so neither is named.
        Hear(ShowBusConstants.KindStagewatch, "7a7b7c7d7e7f", "Hall B", "http", sw.Port);
        Assert.Equal([("link:patterns", "@" + DeskId, "follows")], Now().Select(r => (r.From, r.To, r.Kind)));
        Assert.DoesNotContain("not proved", Words("link:stagewatch"), StringComparison.Ordinal);

        // A link to a name is matched by the host name a hello says, whatever address it came from (a name is not an address a
        // hello could be heard from), and only by that name; a hello no longer heard names nothing.
        Assert.StartsWith("OK @n", await c.AskAsync($"@n LINK PATTERNS localhost:{desk.Port} {deskToken}"), StringComparison.Ordinal);
        Assert.True(await Eventually.TrueAsync(() => Link("patterns").TryGetProperty("host", out var host) && host.GetString() == $"localhost:{desk.Port}"
            && Link("patterns").GetProperty("state").GetString() == "live", 20_000), rig.LogTail());
        Assert.DoesNotContain(Now(), r => r.From == "link:patterns");
        Hear(ShowBusConstants.KindPatterns, "f1f2f3f4f5a6", "Patterns D", "wire", desk.Port, from: "10.9.9.7", host: "LOCALHOST");
        Assert.Contains(("link:patterns", "@f1f2f3f4f5a6", "follows"), Now().Select(r => (r.From, r.To, r.Kind)));
        clock.AdvanceMs(ShowBusConstants.StaleMs + 1);
        Assert.Contains(("link:patterns", "@f1f2f3f4f5a6", "follows"), Now().Select(r => (r.From, r.To, r.Kind)));
        clock.AdvanceMs(ShowBusConstants.GoneMs - ShowBusConstants.StaleMs);
        Assert.Empty(Now());
    }
}
