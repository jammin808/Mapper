using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
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
/// dropped there silently. A busy node's context, posted to Stagewatch, fits Stagewatch's 64 KB at the poster with every link it keeps
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
    /// links live with words, and a Stagewatch frame whose title and message are as long as Stagewatch makes them, with its watermark.</summary>
    private sealed class FullNetwork : INetworkView
    {
        public IReadOnlyList<PeerView> Peers() => [.. Enumerable.Range(0, 128).Select(i => new PeerView(i % 3 == 0 ? "main" : i % 3 == 1 ? "patterns" : "playmap",
            i.ToString("x8", CultureInfo.InvariantCulture), $"Peer {i:000} " + new string('n', 52), i % 4 == 0 ? "amber" : "green",
            $"Peer {i:000}: " + new string('h', 190), true))];

        public LinkView? Patterns() => new("live", "10.0.0.5:9697", true, "paired · pinned 5a7e 91c0 · following its blackout");

        public LinkView? Stagewatch() => new("live", "10.0.0.6:9923", true,
            "Stagewatch's certificate pinned (5a7e 91c0) · showing timer · context trimmed for Stagewatch (left out: 20 recent)");

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

    private static async Task<EngineCore> StressEngineAsync()
    {
        var health = new HealthTests.Outputs
        {
            Now = [.. Enumerable.Range(1, 64).Select(i => new OutputHealth("op_" + i, i != 7, i == 7 ? 0 : 60, i == 12 ? 3 : 0, 4.1, "DP-" + i))],
        };
        var services = HealthTests.Services(new ManualTime(), health, new HealthTests.FakeSampler()) with { Network = new FullNetwork() };
        var engine = new EngineCore(services, StressShow());
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
}
