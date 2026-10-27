using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net.Bus;
using PlayMap.Net.Links;
using PlayMap.Net.Tls;

namespace PlayMap.Net.Tests;

/// <summary>A Stagewatch as the tests play it: /ws records how it was asked (path, query, Authorization), sends each connection
/// its <see cref="Verdict"/> first when one is set (and closes it then when <see cref="CloseAfterVerdict"/>, as Stagewatch does after
/// a viewing-code refusal), then what the test queues, answering pings with pongs, until the test <see cref="Drop"/>s it;
/// /api/peers/context answers a body that declares more than <see cref="MaxContextBytes"/> 413 with Stagewatch's words before it reads
/// it (as Stagewatch's web server does, 64 KB by default), and records any other posted context and answers <see cref="ContextStatus"/>
/// once <see cref="ContextAnswerWhen"/> lets it;
/// /api/proposals serves the approved list for a target, or <see cref="ApprovalsStatus"/> with its body; /api/proposals/&lt;id&gt;/result
/// records each answer and answers the next of <see cref="ResultStatuses"/> (200 once none is left). A test can have each ping answered
/// with a t1 it never carried, or every /ws upgrade refused.</summary>
internal sealed class FakeStagewatch : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Channel<string> _outbox = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<HttpContext, bool> _open = new();
    private int _stopped;

    private FakeStagewatch(WebApplication app) => _app = app;

    public int Port { get; private set; }

    public ConcurrentQueue<string> Asked { get; } = new();

    public ConcurrentQueue<string> Contexts { get; } = new();

    /// <summary>The lengths the context posts answered 413 declared.</summary>
    public ConcurrentQueue<long> Oversize { get; } = new();

    /// <summary>The most a context post's body may declare before it is answered 413 (Stagewatch's own bound by default).</summary>
    public long MaxContextBytes { get; set; } = 64 * 1024;

    public ConcurrentQueue<(string Id, string Body)> Results { get; } = new();

    public ConcurrentQueue<string> Received { get; } = new();

    public string ApprovedJson { get; set; } = "{\"proposals\":[]}";

    /// <summary>The first message each connection hears (Stagewatch 2.18's verdict on a node link), or null for none.</summary>
    public string? Verdict { get; set; }

    /// <summary>The connection is closed, cleanly, just after its verdict is sent.</summary>
    public bool CloseAfterVerdict { get; set; }

    /// <summary>What a context post is answered: 204, or a status with <see cref="ContextAnswer"/> as its body, and
    /// <see cref="ContextRetryAfter"/> seconds in its Retry-After when above 0.</summary>
    public int ContextStatus { get; set; } = 204;

    public string ContextAnswer { get; set; } = "";

    public int ContextRetryAfter { get; set; }

    /// <summary>A context post is answered once this completes (at once by default): a test holds an answer back to land it late.</summary>
    public Task ContextAnswerWhen { get; set; } = Task.CompletedTask;

    /// <summary>What the approved list is answered: 200 with <see cref="ApprovedJson"/>, or a status with <see cref="ApprovalsAnswer"/>.</summary>
    public int ApprovalsStatus { get; set; } = 200;

    public string ApprovalsAnswer { get; set; } = "";

    /// <summary>What each result post is answered, in order; 200 once none is left.</summary>
    public ConcurrentQueue<int> ResultStatuses { get; } = new();

    /// <summary>Each ping is answered with a t1 a second before the one it carried: a pong for a ping never sent, which the clock's
    /// admission alone would take as a sample.</summary>
    public volatile bool ForgedPongs;

    /// <summary>Every /ws upgrade is refused (503): a dial that reaches the server and fails.</summary>
    public volatile bool RefuseSockets;

    /// <summary>The /ws connections asked for so far.</summary>
    public int Sockets => Asked.Count(a => a.StartsWith("ws ", StringComparison.Ordinal));

    public static async Task<FakeStagewatch> StartAsync(System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0, o =>
        {
            if (certificate is not null)
                o.UseHttps(certificate);
        }));
        var app = builder.Build();
        var fake = new FakeStagewatch(app);
        app.UseWebSockets();
        app.Map("/ws", fake.WsAsync);
        app.MapPost("/api/peers/context", async (HttpContext c) =>
        {
            if (c.Request.ContentLength is { } declared && declared > fake.MaxContextBytes)
            {
                fake.Oversize.Enqueue(declared);
                c.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                c.Response.ContentType = "application/json";
                await c.Response.WriteAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{{\"ok\":false,\"error\":\"a request body is at most {fake.MaxContextBytes} bytes; this one declares {declared}\"}}"));
                return;
            }
            using var r = new StreamReader(c.Request.Body);
            fake.Contexts.Enqueue(c.Request.Headers.Authorization + " " + await r.ReadToEndAsync());
            await fake.ContextAnswerWhen;
            c.Response.StatusCode = fake.ContextStatus;
            if (fake.ContextRetryAfter > 0)
                c.Response.Headers.RetryAfter = fake.ContextRetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (fake.ContextAnswer.Length > 0)
            {
                c.Response.ContentType = "application/json";
                await c.Response.WriteAsync(fake.ContextAnswer);
            }
        });
        app.MapGet("/api/proposals", (HttpContext c) =>
        {
            fake.Asked.Enqueue("proposals " + c.Request.QueryString.Value);
            return fake.ApprovalsStatus == 200
                ? Microsoft.AspNetCore.Http.Results.Text(fake.ApprovedJson, "application/json")
                : Microsoft.AspNetCore.Http.Results.Text(fake.ApprovalsAnswer, "application/json", statusCode: fake.ApprovalsStatus);
        });
        app.MapPost("/api/proposals/{id}/result", async (HttpContext c) =>
        {
            using var r = new StreamReader(c.Request.Body);
            fake.Results.Enqueue(((string)c.Request.RouteValues["id"]!, await r.ReadToEndAsync()));
            var status = fake.ResultStatuses.TryDequeue(out var next) ? next : 200;
            return Microsoft.AspNetCore.Http.Results.Text(status == 200 ? "{\"ok\":true}" : "{\"ok\":false,\"error\":\"not now\"}", "application/json", statusCode: status);
        });
        await app.StartAsync();
        fake.Port = new Uri(app.Urls.First()).Port;
        return fake;
    }

    public ValueTask SendAsync(string json) => _outbox.Writer.WriteAsync(json);

    /// <summary>Every connection open now goes, with no close: the link's end is an error.</summary>
    public void Drop()
    {
        foreach (var c in _open.Keys)
            c.Abort();
    }

    private async Task WsAsync(HttpContext c)
    {
        Asked.Enqueue("ws " + c.Request.Path + c.Request.QueryString.Value + " auth=" + c.Request.Headers.Authorization);
        if (RefuseSockets)
        {
            c.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        using var ws = await c.WebSockets.AcceptWebSocketAsync();
        // Only a connection still served is dropped: a context is let go when its request ends.
        _open[c] = true;
        try
        {
            await ServeAsync(c, ws);
        }
        finally
        {
            _open.TryRemove(c, out _);
        }
    }

    private async Task ServeAsync(HttpContext c, WebSocket ws)
    {
        if (Verdict is { } verdict)
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(verdict), WebSocketMessageType.Text, true, c.RequestAborted);
            if (CloseAfterVerdict)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "viewing code", c.RequestAborted);
                return;
            }
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted);
        var sender = Task.Run(async () =>
        {
            await foreach (var m in _outbox.Reader.ReadAllAsync(stop.Token))
                await ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, stop.Token);
        }, CancellationToken.None);
        var buffer = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, stop.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
                var text = Encoding.UTF8.GetString(buffer, 0, r.Count);
                Received.Enqueue(text);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.GetProperty("cmd").GetString() == "ping")
                {
                    var t = doc.RootElement.GetProperty("t").GetInt64() - (ForgedPongs ? 1000 : 0);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await _outbox.Writer.WriteAsync($"{{\"type\":\"pong\",\"t\":{t},\"t2\":{now},\"t3\":{now}}}", stop.Token);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The link closed its end.
        }
        await stop.CancelAsync();
        try
        {
            await sender;
        }
        catch (OperationCanceledException)
        {
            // The sender stops with the socket.
        }
    }

    /// <summary>Stops the Stagewatch: its port refuses every dial from now on. Disposing it after is a no-op.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>A Stagewatch with no certificate as its web server meets a dial: whatever comes is read as a request's head until its blank
/// line. A TLS greeting has none, so nothing is said back (this front waits until the dialler closes, however long that takes, so only
/// the dialler's own deadline ends the attempt); a plain request is passed whole to the <see cref="FakeStagewatch"/> behind it, and
/// its answer back.</summary>
internal sealed class SilentTlsFront : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<TcpClient> _clients = new();
    private readonly int _behind;
    private readonly Task _accept;
    private int _greetings;
    private int _plain;

    public SilentTlsFront(int behind)
    {
        _behind = behind;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptAsync();
    }

    public int Port { get; }

    /// <summary>TLS greetings read and never answered.</summary>
    public int TlsGreetings => Volatile.Read(ref _greetings);

    /// <summary>Plain connections passed to the Stagewatch behind.</summary>
    public int Plain => Volatile.Read(ref _plain);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _clients.Enqueue(client);
                _ = Task.Run(() => ServeAsync(client), CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The front stops.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var ct = _stop.Token;
        try
        {
            var stream = client.GetStream();
            var first = new byte[1];
            if (await stream.ReadAsync(first, ct) == 0)
                return;
            if (first[0] == 0x16)
            {
                Interlocked.Increment(ref _greetings);
                var head = new byte[4096];
                while (await stream.ReadAsync(head, ct) > 0)
                {
                    // Read as a request's head that never ends: nothing is said back.
                }
                return;
            }
            Interlocked.Increment(ref _plain);
            using var behind = new TcpClient();
            await behind.ConnectAsync(IPAddress.Loopback, _behind, ct);
            var back = behind.GetStream();
            await back.WriteAsync(first, ct);
            await Task.WhenAny(stream.CopyToAsync(back, ct), back.CopyToAsync(stream, ct));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // Either end closed.
        }
        finally
        {
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        while (_clients.TryDequeue(out var c))
            c.Dispose();
        await _accept;
        _stop.Dispose();
    }
}

/// <summary>A node whose STATE and context are what a test makes them, every other fact and every line the fake endpoint's.</summary>
internal sealed class ShapedEndpoint(FakeEndpoint inner) : IWireEndpoint
{
    public Func<string>? StateJson { get; init; }

    public Func<string>? ContextJson { get; init; }

    public ValueTask<CommandReply> HandleAsync(WireLine line, SessionInfo session, CancellationToken ct) => inner.HandleAsync(line, session, ct);

    public (string Json, long Revision) State() => StateJson is { } state ? (state(), inner.Seq) : inner.State();

    public string Context() => ContextJson is { } context ? context() : inner.Context();

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

/// <summary>PlayMap as a show app on a Stagewatch: asked for as a node link (its id and kind, with the cid and page an older Stagewatch
/// reads) and the key in a header (never the URL), frames counted on locally and taken latest-wins, "?" while Stagewatch says it is
/// stuck, the operator's redirect raised, pings closing a clock exchange, the context posted, and approved lines collected, run once
/// through the node's own path and answered. Every dial tries TLS first; a pinned certificate, or one the hello names, is TLS or
/// nothing; over plain ws the key goes only to this machine, and off it the timer is read without the key. The pairing is Stagewatch's
/// verdict, or a keyed answer from one that sends none; a refusal holds keyed calls and the next dial, its words kept, until its hold
/// ends; a rate limit holds them and refuses nothing; approved lines are collected only once the key is taken.</summary>
public class StagewatchLinkTests
{
    private const string Frame5 = "{\"type\":\"state\",\"v\":1,\"seq\":5,\"mode\":\"countdown\",\"state\":\"running\",\"seconds\":750,\"text\":\"12:30\",\"total\":1800,\"phase\":\"warning\",\"color\":\"#F59E0B\",\"title\":\"Keynote\",\"next\":\"Panel\",\"message\":\"Wrap up\",\"urgency\":\"warning\",\"flash\":\"slow\",\"blackout\":false,\"holding\":\"\",\"watermark\":\"REHEARSAL\",\"show\":{\"timer\":true,\"message\":true},\"playout\":{\"title\":\"VT1\",\"remaining\":42,\"duration\":60,\"running\":true,\"source\":\"Patterns\"}}";

    private static StagewatchLinkOptions Options(FakeStagewatch sw, bool approvals = false) => new()
    {
        Host = "127.0.0.1",
        Port = sw.Port,
        Key = "swk_test0123456789",
        SelfId = "a1b2c3d4",
        SelfName = "Stage left",
        Approvals = approvals,
        BeatEvery = TimeSpan.FromMilliseconds(200),
        ContextEvery = TimeSpan.FromMilliseconds(200),
        ApprovalsEvery = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task AStuckBeatShowsAQuestionMarkAndARedirectIsRaised()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        var commands = new ConcurrentQueue<StagewatchScreenCommand>();
        // The link counts a frame on from its arrival on a clock only the test moves, so the reading a second on is the frame
        // counted on by exactly that second, however late the test reads it.
        var mono = new ManualClock(1_000_000_000);
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), mono);
        link.ScreenCommand += commands.Enqueue;
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live));
        var asked = Assert.Single(sw.Asked);
        Assert.Equal("ws /ws?node=a1b2c3d4&t=playmap&cid=a1b2c3d4&page=/playmap&detail=1 auth=Bearer swk_test0123456789", asked);

        Assert.Equal("?", link.Reading.Text);
        await sw.SendAsync(Frame5);
        Assert.True(await Eventually.TrueAsync(() => link.Reading.Trusted));
        var reading = link.Reading.Timer!;
        Assert.Equal("Keynote", reading.Title);
        Assert.Equal("#F59E0B", reading.Color);
        Assert.Equal("REHEARSAL", reading.Watermark);
        Assert.Equal(42, reading.Playout!.Remaining);
        Assert.True(reading.Show["timer"]);
        Assert.Equal((750.0, "12:30"), (link.Reading.Timer!.Seconds, link.Reading.Text));
        mono.AdvanceMs(1100);
        var counted = link.Reading;
        Assert.Equal((748.9, "12:29", true), (Math.Round(counted.Timer!.Seconds, 6), counted.Text, counted.Trusted));

        await sw.SendAsync(Frame5.Replace("\"seq\":5", "\"seq\":4", StringComparison.Ordinal).Replace("\"Keynote\"", "\"Old\"", StringComparison.Ordinal));
        await sw.SendAsync("{\"type\":\"beat\",\"live\":false}");
        Assert.True(await Eventually.TrueAsync(() => link.Stuck));
        Assert.Equal("?", link.Reading.Text);
        Assert.False(link.Reading.Trusted);
        Assert.Equal("Keynote", link.LastFrame!.Title);   // the older seq did not overwrite the newer frame

        await sw.SendAsync(Frame5.Replace("\"seq\":5", "\"seq\":6", StringComparison.Ordinal));
        Assert.True(await Eventually.TrueAsync(() => link.Reading.Trusted));

        await sw.SendAsync("{\"type\":\"redirect\",\"to\":\"/layout?name=Keynote\"}");
        Assert.True(await Eventually.TrueAsync(() => !commands.IsEmpty));
        var redirect = Assert.Single(commands);
        Assert.Equal(("redirect", "/layout?name=Keynote"), (redirect.Type, redirect.To));

        Assert.True(await Eventually.TrueAsync(() => link.Clock.Samples > 0));
        Assert.True(await Eventually.TrueAsync(() => !sw.Contexts.IsEmpty));
        Assert.StartsWith("Bearer swk_test0123456789 {\"v\":1,\"node\":{\"t\":\"playmap\"", sw.Contexts.First(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovedLinesAreCollectedRunOnceAndAnswered()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.ApprovedJson = "{\"proposals\":[{\"id\":\"p17\",\"from\":{\"t\":\"patterns\",\"id\":\"9f8e7d6c\"},\"target\":{\"t\":\"playmap\",\"id\":\"a1b2c3d4\"},\"line\":\"BLACKOUT ON\",\"risk\":\"high\",\"state\":\"approved\"},"
            + "{\"id\":\"p18\",\"target\":{\"t\":\"playmap\",\"id\":\"ffff0000\"},\"line\":\"BLACKOUT OFF\",\"state\":\"approved\"}]}";
        var endpoint = new FakeEndpoint();
        await using var link = new StagewatchLink(Options(sw, approvals: true), endpoint, TestSettings.Wall(), new PinStore());
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => !sw.Results.IsEmpty));
        await Task.Delay(500);   // several more polls: the same approved list must not run twice
        var (id, body) = Assert.Single(sw.Results);
        Assert.Equal("p17", id);
        Assert.Equal("{\"ok\":true,\"words\":\"blackout on\"}", body);
        Assert.True(endpoint.Blackout);
        var (line, session) = Assert.Single(endpoint.Lines);
        Assert.Equal("BLACKOUT", line.Verb);
        Assert.Equal("approval", session.Transport);
        Assert.Contains(sw.Asked, a => a == "proposals ?target=a1b2c3d4&state=approved");
        Assert.Equal(1, link.ApprovalsRun);
    }

    [Fact]
    public async Task OverTlsTheCertificateIsPinnedOnFirstUseAndAChangeWaitsForTheOperator()
    {
        var dirA = Directory.CreateTempSubdirectory("playmap-sw-a-");
        var dirB = Directory.CreateTempSubdirectory("playmap-sw-b-");
        try
        {
            using var a = TlsIdentity.LoadOrCreate(dirA.FullName, "Hall A", DateTimeOffset.UtcNow);
            using var b = TlsIdentity.LoadOrCreate(dirB.FullName, "Hall A", DateTimeOffset.UtcNow);
            var pins = new PinStore();
            await using (var sw = await FakeStagewatch.StartAsync(a.Certificate))
            {
                await using var link = new StagewatchLink(Options(sw) with { Tls = true, Fingerprint = a.Fingerprint, PinKey = "node:5a5a5a5a" }, new FakeEndpoint(), TestSettings.Wall(), pins);
                link.Start();
                Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live));
                Assert.True(await Eventually.TrueAsync(() => pins.Get("node:5a5a5a5a") == a.Fingerprint));
                Assert.Equal(PinVerdict.Pinned, link.Pin);
            }

            // Another machine answers at the node's address with another certificate: refused until accepted.
            await using var other = await FakeStagewatch.StartAsync(b.Certificate);
            await using var second = new StagewatchLink(Options(other) with { Tls = true, Fingerprint = b.Fingerprint, PinKey = "node:5a5a5a5a" }, new FakeEndpoint(), TestSettings.Wall(), pins);
            second.Start();
            Assert.True(await Eventually.TrueAsync(() => second.PendingFingerprint == b.Fingerprint));
            Assert.NotEqual(LinkStatus.Live, second.Status);
            Assert.StartsWith("CERTIFICATE CHANGED", second.Words, StringComparison.Ordinal);
            Assert.Empty(other.Asked);
            Assert.StartsWith("Accepted", second.AcceptPendingCertificate(), StringComparison.Ordinal);
            Assert.Equal(b.Fingerprint, pins.Get("node:5a5a5a5a"));
            Assert.True(await Eventually.TrueAsync(() => second.Status == LinkStatus.Live, 10000));
        }
        finally
        {
            dirA.Delete(recursive: true);
            dirB.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ATypedAddressTriesTlsFirstAndTheKeyRidesOverIt()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-sw-typed-");
        try
        {
            using var id = TlsIdentity.LoadOrCreate(dir.FullName, "Hall A", DateTimeOffset.UtcNow);
            var pins = new PinStore();
            await using var sw = await FakeStagewatch.StartAsync(id.Certificate);
            // Typed, no hello names it, and played as another machine: it answers TLS, so the key goes, inside TLS.
            var pinKey = PinStore.AddressKey("127.0.0.1", sw.Port);
            await using var link = new StagewatchLink(Options(sw) with { PinKey = pinKey, IsLocal = _ => false }, new FakeEndpoint(), TestSettings.Wall(), pins);
            link.Start();
            Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live), link.Words);
            Assert.Equal((true, false), (link.OverTls, link.KeyHeldBack));
            Assert.Equal("ws /ws?node=a1b2c3d4&t=playmap&cid=a1b2c3d4&page=/playmap&detail=1 auth=Bearer swk_test0123456789", Assert.Single(sw.Asked));
            // Its certificate is pinned by the address once the key's first side call is answered.
            Assert.True(await Eventually.TrueAsync(() => pins.Get(pinKey) == id.Fingerprint), link.Words);
            Assert.StartsWith("Bearer swk_test0123456789 ", sw.Contexts.First(), StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task APlainStagewatchOffThisMachineIsReadWithoutTheKey()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        var pins = new PinStore();
        var pinKey = PinStore.AddressKey("127.0.0.1", sw.Port);
        // Its silence is counted on a clock only the test moves, so a slow runner never drops the link while the test watches it.
        await using var link = new StagewatchLink(Options(sw, approvals: true) with { PinKey = pinKey, IsLocal = _ => false }, new FakeEndpoint(), TestSettings.Wall(), pins,
            new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live), link.Words);

        // It offers no TLS and is not this machine: the upgrade carries no key, and the words say why.
        Assert.Equal("ws /ws?node=a1b2c3d4&t=playmap&cid=a1b2c3d4&page=/playmap&detail=1 auth=", Assert.Single(sw.Asked));
        Assert.Equal((false, true), (link.OverTls, link.KeyHeldBack));
        Assert.StartsWith($"Stagewatch at 127.0.0.1:{sw.Port} offers no TLS — the key stays on this machine", link.Words, StringComparison.Ordinal);

        // The timer is still read; no side call (each would carry the key) is made, and nothing is pinned.
        await sw.SendAsync(Frame5);
        Assert.True(await Eventually.TrueAsync(() => link.Reading.Trusted));
        Assert.True(await Eventually.TrueAsync(() => link.Clock.Samples > 0));
        // The ping, the context and the approval queue share one beat, all three due on its first turn and every 200 ms after: the
        // third ping is a turn that would have posted the context and asked the queue twice already.
        Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= 3), $"{Pings(sw)} pings");
        Assert.Empty(sw.Contexts);
        Assert.Single(sw.Asked);
        Assert.Null(pins.Get(pinKey));
        Assert.Equal(1, link.Connections);
    }

    [Fact]
    public async Task APinnedStagewatchIsNeverDialledPlain()
    {
        // Its certificate is pinned (its hello names none now): a plain answer at its address is refused, and nothing goes to it.
        await using var sw = await FakeStagewatch.StartAsync();
        var pins = new PinStore();
        pins.Pin("node:5a5a5a5a", "sha256:" + new string('a', 64));
        await using var link = new StagewatchLink(Options(sw) with { PinKey = "node:5a5a5a5a" }, new FakeEndpoint(), TestSettings.Wall(), pins);
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Words.Contains("answered without its pinned certificate — refused", StringComparison.Ordinal)), link.Words);
        Assert.NotEqual(LinkStatus.Live, link.Status);
        Assert.Equal(0, link.Connections);
        Assert.Empty(sw.Asked);
        Assert.Empty(sw.Contexts);
    }

    [Fact]
    public async Task AStagewatchWhoseHelloNamesACertificateIsTlsOrNothing()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        await using var link = new StagewatchLink(Options(sw) with { Tls = true, Fingerprint = "sha256:" + new string('b', 64), PinKey = "node:6b6b6b6b" },
            new FakeEndpoint(), TestSettings.Wall(), new PinStore());
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Words.Contains("answered without TLS although its hello names a certificate — refused", StringComparison.Ordinal)),
            link.Words);
        Assert.Equal(0, link.Connections);
        Assert.Empty(sw.Asked);
    }

    [Fact]
    public async Task APinnedIdWhoseHelloNamesNoTlsDialsTls()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-sw-pinned-");
        try
        {
            using var id = TlsIdentity.LoadOrCreate(dir.FullName, "Hall A", DateTimeOffset.UtcNow);
            await using var sw = await FakeStagewatch.StartAsync(id.Certificate);
            var pins = new PinStore();
            pins.Pin(PinStore.NodeKey("5a5a5a5a"), id.Fingerprint);
            // Heard on the hello with its ws port and no certificate named: the pin, not the hello, says TLS.
            var hello = new ShowBusHello(ShowBusConstants.KindStagewatch, 1, 1, "5a5a5a5a", "Hall A", "127.0.0.1",
                new Dictionary<string, int> { ["ws"] = sw.Port }, [], 0, null, null);
            var options = StagewatchLinkOptions.FromPeer(new ShowBusPeer(hello, "127.0.0.1", 0, 0, PeerLiveness.Live), "swk_test0123456789", "a1b2c3d4", "Stage left");
            Assert.False(options.Tls);
            await using var link = new StagewatchLink(options with { IsLocal = _ => false, BeatEvery = TimeSpan.FromMilliseconds(200) },
                new FakeEndpoint(), TestSettings.Wall(), pins);
            link.Start();
            Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live), link.Words);
            Assert.Equal((true, (PinVerdict?)PinVerdict.Pinned, false), (link.OverTls, link.Pin, link.KeyHeldBack));
            Assert.Equal("ws /ws?node=a1b2c3d4&t=playmap&cid=a1b2c3d4&page=/playmap&detail=1 auth=Bearer swk_test0123456789", Assert.Single(sw.Asked));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStagewatchThatReadsTheTlsGreetingAsAHeadIsDialledPlainAtTheLinksOwnDeadline(bool local)
    {
        // A Stagewatch with HTTPS off reads the greeting and waits for a blank line that never comes. The front behind this address never
        // answers and never closes, so the link's own deadline ends the TLS attempt, and the link goes live over plain ws: with the key
        // on this machine, read without it off this machine.
        await using var sw = await FakeStagewatch.StartAsync();
        await using var front = new SilentTlsFront(sw.Port);
        var pins = new PinStore();
        var pinKey = PinStore.AddressKey("127.0.0.1", front.Port);
        var options = Options(sw) with { Port = front.Port, PinKey = pinKey, IsLocal = _ => local };
        // Under the 5 s Stagewatch waits for a request's head: the link, not Stagewatch, ends the attempt.
        Assert.InRange(options.TlsAnswerWithin, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2.5));
        await using var link = new StagewatchLink(options, new FakeEndpoint(), TestSettings.Wall(), pins, new ManualClock(1_000_000_000));
        link.Start();

        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live, 15000), link.Words);
        Assert.Equal(1, front.TlsGreetings);
        Assert.True(front.Plain >= 1);
        Assert.Equal((false, !local), (link.OverTls, link.KeyHeldBack));
        Assert.Equal("ws /ws?node=a1b2c3d4&t=playmap&cid=a1b2c3d4&page=/playmap&detail=1 auth=" + (local ? "Bearer swk_test0123456789" : ""), Assert.Single(sw.Asked));
        Assert.Equal(1, link.Connections);
        Assert.Null(pins.Get(pinKey));
    }

    [Fact]
    public async Task APinnedStagewatchThatShowsNoCertificateByTheDeadlineIsRefused()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        await using var front = new SilentTlsFront(sw.Port);
        var pins = new PinStore();
        pins.Pin("node:5a5a5a5a", "sha256:" + new string('a', 64));
        await using var link = new StagewatchLink(Options(sw) with { Port = front.Port, PinKey = "node:5a5a5a5a", TlsAnswerWithin = TimeSpan.FromMilliseconds(300) },
            new FakeEndpoint(), TestSettings.Wall(), pins);
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Words.Contains("answered without its pinned certificate — refused", StringComparison.Ordinal)), link.Words);
        Assert.True(front.TlsGreetings >= 1);
        Assert.Equal(0, front.Plain);
        Assert.Equal(0, link.Connections);
        Assert.Empty(sw.Asked);
    }

    [Fact]
    public async Task ATypedAddressWithNoKeyIsNeverPinned()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-sw-nokey-");
        try
        {
            using var id = TlsIdentity.LoadOrCreate(dir.FullName, "Hall A", DateTimeOffset.UtcNow);
            var pins = new PinStore();
            await using var sw = await FakeStagewatch.StartAsync(id.Certificate);
            var pinKey = PinStore.AddressKey("127.0.0.1", sw.Port);
            // No key: no answer is authenticated, so whatever certificate answers at the typed address is used and never pinned.
            await using var link = new StagewatchLink(Options(sw) with { Key = "", PinKey = pinKey, IsLocal = _ => false }, new FakeEndpoint(), TestSettings.Wall(), pins,
                new ManualClock(1_000_000_000));
            link.Start();
            Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live), link.Words);
            // The beat starts after the pin's decision: its first ping says the decision was made.
            Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= 1), link.Words);
            Assert.Equal((true, (PinVerdict?)PinVerdict.FirstUse), (link.OverTls, link.Pin));
            Assert.Null(pins.Get(pinKey));
            Assert.Equal("ws /ws?node=a1b2c3d4&t=playmap&cid=a1b2c3d4&page=/playmap&detail=1 auth=", Assert.Single(sw.Asked));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static int Pings(FakeStagewatch sw) => sw.Received.Count(m => m.Contains("\"cmd\":\"ping\"", StringComparison.Ordinal));

    private const string VerdictKey = "{\"type\":\"auth\",\"ok\":true,\"scope\":\"node\",\"node\":{\"t\":\"playmap\",\"id\":\"a1b2c3d4\",\"verified\":\"key\"}}";

    private const string Unknown = "{\"type\":\"auth\",\"ok\":false,\"error\":\"unknown or revoked API key\",\"retry\":60}";

    private const string SpeaksForPatterns = "this key speaks for patterns 9f8e7d6c — make a key for playmap a1b2c3d4";

    [Theory]
    [InlineData("a1b2c3d4", "/ws?node=a1b2c3d4&t=playmap&cid=a1b2c3d4&page=/playmap&detail=1")]
    [InlineData("abc", "/ws?node=abc&t=playmap&cid=playmap-abc&page=/playmap&detail=1")]
    [InlineData("node_1-of-a-hall-with-a-long-name-x", "/ws?node=node_1-of-a-hall-with-a-long-name-x&t=playmap&cid=playmap-node_1-of-a-hall-with-a-long-name-x&page=/playmap&detail=1")]
    [InlineData("a b", "/ws?cid=playmap-a%20b&page=/playmap&detail=1")]
    [InlineData("12345678901234567890123456789012345678901", "/ws?cid=playmap-12345678901234567890123456789012345678901&page=/playmap&detail=1")]
    public void ANodeLinkNamesTheNodesIdAndKindAndAnIdThatIsNoShowBusIdDialsAsAScreen(string selfId, string path) =>
        Assert.Equal(path, StagewatchLink.SocketPath(selfId));

    [Fact]
    public async Task AVerdictOfItsKeyPairsTheLinkAndNothingKeyedGoesBeforeIt()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = VerdictKey;
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired), link.Words);
        Assert.Equal("ws /ws?node=a1b2c3d4&t=playmap&cid=a1b2c3d4&page=/playmap&detail=1 auth=Bearer swk_test0123456789", Assert.Single(sw.Asked));
        Assert.Equal(("", TimeSpan.Zero), (link.Refusal, link.RefusedFor));
        Assert.True(await Eventually.TrueAsync(() => !sw.Contexts.IsEmpty), link.Words);
        Assert.Equal(PairState.Paired, link.Pairing);
    }

    [Fact]
    public async Task ALinkWithNoKeyNeedsNoPairingWhateverTheVerdictSays()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = "{\"type\":\"auth\",\"ok\":true,\"scope\":\"none\",\"node\":{\"t\":\"playmap\",\"id\":\"a1b2c3d4\",\"verified\":\"none\"},\"words\":\"no key — the id is only claimed\"}";
        await using var link = new StagewatchLink(Options(sw) with { Key = "" }, new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live), link.Words);
        // The beat's third ping goes after the verdict was read (its first message): the pairing it left is the verdict's.
        Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= 3), $"{Pings(sw)} pings");
        Assert.Equal((PairState.NotNeeded, ""), (link.Pairing, link.Refusal));
        Assert.Equal("ws /ws?node=a1b2c3d4&t=playmap&cid=a1b2c3d4&page=/playmap&detail=1 auth=", Assert.Single(sw.Asked));
        Assert.Empty(sw.Contexts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARefusedKeyIsHeldBeforeAnythingElseAndTheLinkDialsOnceInItsHold(bool dropped)
    {
        // Stagewatch refuses the key and keeps the socket for the public frame; or, once the verdict is read, the socket goes with no
        // close, so the connection ends in an error: the hold was recorded as the verdict was read, and the next dial waits it either way.
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = Unknown;
        await using var link = new StagewatchLink(Options(sw, approvals: true), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Refused), link.Words);
        Assert.Equal(StagewatchLink.KeyRefused + "unknown or revoked API key", link.Refusal);
        Assert.Equal(StagewatchLink.RefusedHold, link.RefusedFor);
        Assert.StartsWith($"Stagewatch at 127.0.0.1:{sw.Port} refused this node's key — unknown or revoked API key", link.Words, StringComparison.Ordinal);
        if (dropped)
        {
            sw.Drop();
            Assert.True(await Eventually.TrueAsync(() => link.Status != LinkStatus.Live), link.Words);
            await Task.Delay(3000);
            Assert.Equal((1, 1L), (sw.Sockets, link.Connections));
            Assert.Equal(PairState.Refused, link.Pairing);
            Assert.StartsWith($"Stagewatch at 127.0.0.1:{sw.Port} refused this node's key", link.Words, StringComparison.Ordinal);
        }
        else
        {
            // The frame keeps coming; the beat runs, and no keyed call goes while the hold stands.
            await sw.SendAsync(Frame5);
            Assert.True(await Eventually.TrueAsync(() => link.Reading.Trusted));
            Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= 3), $"{Pings(sw)} pings");
            Assert.Equal(LinkStatus.Live, link.Status);
            Assert.Equal(1, link.Connections);
        }
        Assert.Empty(sw.Contexts);
        Assert.DoesNotContain(sw.Asked, a => a.StartsWith("proposals", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFreshVerdictOfItsKeyLiftsTheRefusalAndItsHold()
    {
        // The operator lets the key speak for this node: Stagewatch says so on the open socket, and the keyed calls go again.
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = Unknown;
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        var moves = new ConcurrentQueue<PairState>();
        link.PairingChanged += moves.Enqueue;
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Refused), link.Words);
        Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= 2), $"{Pings(sw)} pings");
        Assert.Empty(sw.Contexts);
        await sw.SendAsync(VerdictKey);
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired), link.Words);
        Assert.Equal(("", TimeSpan.Zero), (link.Refusal, link.RefusedFor));
        Assert.Equal("following Stagewatch at 127.0.0.1:" + sw.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), link.Words);
        Assert.True(await Eventually.TrueAsync(() => !sw.Contexts.IsEmpty), link.Words);
        // Each move is told once (the context's answers that only say it again are not), so every reader hears it.
        Assert.Equal([PairState.Refused, PairState.Paired], moves);
    }

    [Fact]
    public async Task AVerdictThatTheKeySpeaksForNoNodeRefusesItWithStagewatchsWords()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = "{\"type\":\"auth\",\"ok\":true,\"scope\":\"node\",\"node\":{\"t\":\"playmap\",\"id\":\"a1b2c3d4\",\"verified\":\"none\"},\"words\":\"" + SpeaksForPatterns + "\"}";
        // Its node routes refuse the key as its verdict did.
        sw.ContextStatus = 403;
        sw.ContextAnswer = "{\"ok\":false,\"error\":\"" + SpeaksForPatterns + "\"}";
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Refused), link.Words);
        Assert.Equal(StagewatchLink.KeyRefused + SpeaksForPatterns, link.Refusal);
        // A verdict with no retry holds nothing; the context's 403 then holds the key, so one post is all that goes.
        Assert.True(await Eventually.TrueAsync(() => link.RefusedFor > TimeSpan.Zero), link.Words);
        Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= 4), $"{Pings(sw)} pings");
        Assert.Single(sw.Contexts);
        Assert.Equal((PairState.Refused, StagewatchLink.KeyRefused + SpeaksForPatterns), (link.Pairing, link.Refusal));
    }

    [Fact]
    public async Task AStagewatchThatSendsNoVerdictPairsTheLinkOnAKeyedAnswer()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => !sw.Contexts.IsEmpty), link.Words);
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired), link.Words);
        // Nothing keyed went before the connection's first message: the pong to its first ping.
        Assert.Contains(sw.Received, m => m.Contains("\"cmd\":\"ping\"", StringComparison.Ordinal));
    }

    /// <summary>A context as the node builds it, with <paramref name="rows"/> journal rows of <paramref name="chars"/> characters each and
    /// a headline of <paramref name="headline"/> characters.</summary>
    private static string BigContext(int rows, int chars, int headline = 4) => new ShowContext("playmap", "a1b2c3d4", "Stage left", 1_760_000_000_000,
        new string('h', headline), [new ContextItem("show", "show", "Show", Light.Green, "rehearsal")], [],
        [.. Enumerable.Range(0, rows).Select(i => new ContextRecent(1_760_000_000_000 + i, $"Show: {i:00} " + new string('x', chars)))],
        ShowContext.CommandsFromCatalog(), []).ToJson();

    /// <summary>The body a context post carried, without the Authorization the fake records before it.</summary>
    private static string Body(string recorded) => recorded[(recorded.IndexOf(' ', "Bearer ".Length) + 1)..];

    /// <summary>A context as the node builds it with <paramref name="links"/> links, too many for Stagewatch's 64 KB even with its journal
    /// rows within their size, so a post leaves links out.</summary>
    private static string Crowded(int links) => new ShowContext("playmap", "a1b2c3d4", "Stage left", 1_760_000_000_000, "busy",
        [new ContextItem("show", "show", "Show", Light.Green, "rehearsal")],
        [.. Enumerable.Range(0, links).Select(i => new ContextLink("output:" + i, "output:" + (i + 1), "feeds", i % 2 == 0 ? Light.Grey : Light.Green))],
        [.. Enumerable.Range(0, 20).Select(i => new ContextRecent(1_760_000_000_000 + i, $"Show: {i:00} " + new string('x', 400)))],
        ShowContext.CommandsFromCatalog(), []).ToJson();

    /// <summary>The state of the context:trimmed item a posted body carries, or null for none.</summary>
    private static string? TrimmedState(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("id").GetString() == ContextFit.TrimmedId).Select(i => i.GetProperty("state").GetString()).SingleOrDefault();
    }

    /// <summary>The link's log, each line with its level.</summary>
    private sealed class KeptLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Line)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task AContextOverStagewatchsBytesOnlyByItsLongRowsIsPostedFittedAndSaidSo()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        var built = BigContext(rows: 20, chars: 8000);
        Assert.True(Encoding.UTF8.GetByteCount(built) > 160_000);
        var endpoint = new ShapedEndpoint(new FakeEndpoint()) { ContextJson = () => built };
        await using var link = new StagewatchLink(Options(sw), endpoint, TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => !sw.Contexts.IsEmpty && link.ContextWords.Length > 0), link.Words);

        // Stagewatch took it whole: within its 64 KB, every journal row cut to the 200 characters its reader keeps, and nothing else moved,
        // so nothing was left out and no context:trimmed item says so (§6's sizes, not its trimming).
        var body = Body(sw.Contexts.First());
        Assert.Null(TrimmedState(body));
        Assert.Equal(ContextFit.ForPost(built).Body, body);
        Assert.InRange(Encoding.UTF8.GetByteCount(body), 1, ContextFit.MaxBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), link.ContextBytes);
        Assert.Empty(sw.Oversize);
        using (var doc = JsonDocument.Parse(body))
        {
            var recent = doc.RootElement.GetProperty("recent").EnumerateArray().Select(r => r.GetProperty("text").GetString()!).ToList();
            Assert.Equal(20, recent.Count);
            Assert.All(recent, r => Assert.Equal(ContextFit.RecentChars, r.Length));
            Assert.Equal(ShowContext.CommandsFromCatalog().Count, doc.RootElement.GetProperty("commands").GetArrayLength());
        }

        // The link says so in its words (and so in STATE's links.stagewatch.detail), as a fit, not a trim, and the post paired it.
        const string fitted = "context fitted to Stagewatch's 64 KB: 20 recent lines cut to Show Bus §6's 200 characters, all its reader keeps of one; nothing left out";
        Assert.Equal(fitted, link.ContextWords);
        Assert.EndsWith(" · " + fitted, " · " + link.Words, StringComparison.Ordinal);
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired), link.Words);
    }

    /// <summary>A show whose context is over Stagewatch's bytes at every post: each post leaves out what it must and says so in the item
    /// Stagewatch takes and in the link's words, whose counts follow the show; the log says it once, and again only when what the post
    /// did changes (taken as built, then trimmed again), never at every post.</summary>
    [Fact]
    public async Task AContextTrimmedAtEveryPostSaysItsCountsEachTimeAndLogsOnlyAChangeOfWhatThePostDid()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        var calls = 0;
        var crowded = 1;
        // Each post's context has one link more than the last: what the post leaves out moves at every post.
        string Built() => Volatile.Read(ref crowded) == 1 ? Crowded(600 + Interlocked.Increment(ref calls)) : BigContext(rows: 2, chars: 10);
        var log = new KeptLogger();
        var endpoint = new ShapedEndpoint(new FakeEndpoint()) { ContextJson = Built };
        await using var link = new StagewatchLink(Options(sw), endpoint, TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000), log);
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => sw.Contexts.Count >= 4), link.Words);

        var said = sw.Contexts.Select(c => TrimmedState(Body(c))).ToList();
        Assert.All(said, s => Assert.StartsWith("left out: ", s, StringComparison.Ordinal));
        Assert.True(said.Distinct(StringComparer.Ordinal).Count() >= 2, string.Join(" | ", said));
        Assert.All(sw.Contexts, c => Assert.InRange(Encoding.UTF8.GetByteCount(Body(c)), 1, ContextFit.MaxBytes));
        Assert.StartsWith("context trimmed for Stagewatch (left out: ", link.ContextWords, StringComparison.Ordinal);
        bool Trims((LogLevel Level, string Line) l) => l.Line.Contains("context trimmed for Stagewatch", StringComparison.Ordinal);
        Assert.Equal(LogLevel.Information, Assert.Single(log.Lines, Trims).Level);

        // Taken as built: the words go, and nothing is logged for it.
        Volatile.Write(ref crowded, 0);
        Assert.True(await Eventually.TrueAsync(() => link.ContextWords.Length == 0), link.Words);
        Assert.Single(log.Lines, Trims);

        // Trimmed again: said again, and logged once more.
        var before = sw.Contexts.Count;
        Volatile.Write(ref crowded, 1);
        Assert.True(await Eventually.TrueAsync(() => sw.Contexts.Count >= before + 3 && link.ContextWords.Length > 0), link.Words);
        Assert.Equal(2, log.Lines.Count(Trims));
        Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Warning && l.Line.Contains("context", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AContextNothingOfWhichFitsIsNotPostedAndSaidSo()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        // The headline is never trimmed: a node whose headline alone is 70 KB has nothing to post.
        var endpoint = new ShapedEndpoint(new FakeEndpoint()) { ContextJson = () => BigContext(rows: 2, chars: 10, headline: 70_000) };
        await using var link = new StagewatchLink(Options(sw), endpoint, TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.ContextWords.Length > 0), link.Words);
        var said = System.Text.RegularExpressions.Regex.Match(link.ContextWords, "^this node's context is ([0-9]+) KB after trimming — over Stagewatch's 64 KB; not posted$",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        Assert.True(said.Success, link.ContextWords);
        Assert.InRange(int.Parse(said.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 69, 70);
        Assert.Contains(link.ContextWords, link.Words, StringComparison.Ordinal);
        // Pings and the context's turns both come every 200 ms: by the third ping its turn has come again, and nothing was posted.
        Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= 3), $"{Pings(sw)} pings");
        Assert.Empty(sw.Contexts);
        Assert.Empty(sw.Oversize);
        Assert.Equal(0, link.ContextBytes);
        Assert.Equal(LinkStatus.Live, link.Status);
        Assert.Equal(0, link.SideCallFaults(StagewatchSideCall.Context));
    }

    [Fact]
    public async Task AStagewatchThatAnswers413SaysItsWordsAndHoldsNothing()
    {
        // A Stagewatch whose bound is under §14's: it answers a body over it 413, drained, before any route reads it.
        await using var sw = await FakeStagewatch.StartAsync();
        sw.MaxContextBytes = 1024;
        var built = BigContext(rows: 3, chars: 100);
        var bytes = Encoding.UTF8.GetByteCount(built);
        Assert.InRange(bytes, 1025, ContextFit.MaxBytes);
        var endpoint = new ShapedEndpoint(new FakeEndpoint()) { ContextJson = () => built };
        await using var link = new StagewatchLink(Options(sw), endpoint, TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => sw.Oversize.Count >= 2), link.Words);

        // Its words are kept and said; the key is not refused, nothing is held, and the next post goes on its turn.
        Assert.Equal($"Stagewatch at 127.0.0.1:{sw.Port} refused this node's context as too large (413) — a request body is at most 1024 bytes; this one declares {bytes}",
            link.ContextWords);
        Assert.Contains(link.ContextWords, link.Words, StringComparison.Ordinal);
        Assert.All(sw.Oversize, declared => Assert.Equal(bytes, declared));
        Assert.Empty(sw.Contexts);
        Assert.NotEqual(PairState.Refused, link.Pairing);
        Assert.Equal((TimeSpan.Zero, TimeSpan.Zero, ""), (link.RefusedFor, link.BusyFor, link.Refusal));
        Assert.Equal(LinkStatus.Live, link.Status);

        // Taken again once Stagewatch takes it: the words go.
        sw.MaxContextBytes = ContextFit.MaxBytes;
        Assert.True(await Eventually.TrueAsync(() => !sw.Contexts.IsEmpty && link.ContextWords.Length == 0), link.Words);
        Assert.Equal(built, Body(sw.Contexts.First()));
        Assert.DoesNotContain("413", link.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContextOfTheNodesOwnSizeIsPostedByteForByteAndSaysNothing()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        var built = BigContext(rows: 20, chars: 150);
        var endpoint = new ShapedEndpoint(new FakeEndpoint()) { ContextJson = () => built };
        await using var link = new StagewatchLink(Options(sw), endpoint, TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => sw.Contexts.Count >= 2), link.Words);
        Assert.All(sw.Contexts, c => Assert.Equal(built, Body(c)));
        Assert.Equal(("", Encoding.UTF8.GetByteCount(built)), (link.ContextWords, link.ContextBytes));
        Assert.DoesNotContain("context", link.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContextRefusedWith403SaysStagewatchsWordsAndHoldsEveryKeyedCall()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.ContextStatus = 403;
        sw.ContextAnswer = "{\"ok\":false,\"error\":\"" + SpeaksForPatterns + "\"}";
        await using var link = new StagewatchLink(Options(sw, approvals: true), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Refused), link.Words);
        Assert.Equal(StagewatchLink.KeyRefused + SpeaksForPatterns, link.Refusal);
        Assert.Equal($"Stagewatch at 127.0.0.1:{sw.Port} refused this node's context — {SpeaksForPatterns}", link.Words);
        Assert.Equal(StagewatchLink.RefusedHold, link.RefusedFor);
        // The context and the approval queue share the beat: after the 403 the queue is not asked and the context not posted again.
        Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= 4), $"{Pings(sw)} pings");
        Assert.Single(sw.Contexts);
        Assert.DoesNotContain(sw.Asked, a => a.StartsWith("proposals", StringComparison.Ordinal));
        Assert.Equal(LinkStatus.Live, link.Status);
    }

    [Fact]
    public async Task ARefusedCollectIsSaidAndOneRefusedForTheKeyHoldsIt()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = VerdictKey;
        sw.ApprovalsStatus = 400;
        sw.ApprovalsAnswer = "{\"ok\":false,\"error\":\"target must be a node id\"}";
        await using var link = new StagewatchLink(Options(sw, approvals: true), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Words.Contains("refused this node's collect", StringComparison.Ordinal)), link.Words);
        Assert.Equal($"Stagewatch at 127.0.0.1:{sw.Port} refused this node's collect — target must be a node id", link.Words);
        // Not the key's refusal: the link stays paired and asks again.
        Assert.Equal((PairState.Paired, TimeSpan.Zero), (link.Pairing, link.RefusedFor));
        Assert.True(await Eventually.TrueAsync(() => sw.Asked.Count(a => a.StartsWith("proposals", StringComparison.Ordinal)) >= 2));

        sw.ApprovalsStatus = 403;
        sw.ApprovalsAnswer = "{\"ok\":false,\"error\":\"" + SpeaksForPatterns + "\"}";
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Refused), link.Words);
        Assert.Equal(StagewatchLink.KeyRefused + SpeaksForPatterns, link.Refusal);
        Assert.Equal($"Stagewatch at 127.0.0.1:{sw.Port} refused this node's collect — {SpeaksForPatterns}", link.Words);
        Assert.Equal(StagewatchLink.RefusedHold, link.RefusedFor);
        var asked = sw.Asked.Count(a => a.StartsWith("proposals", StringComparison.Ordinal));
        var posted = sw.Contexts.Count;
        var pings = Pings(sw);
        Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= pings + 3), $"{Pings(sw)} pings");
        Assert.Equal((asked, posted), (sw.Asked.Count(a => a.StartsWith("proposals", StringComparison.Ordinal)), sw.Contexts.Count));
    }

    [Fact]
    public async Task TheOperatorsDisconnectIsHeldBeforeTheCloseAndWaitedHoweverTheConnectionEnds()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = "{\"type\":\"disconnect\"}";
        var heldAtCommand = TimeSpan.Zero;
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.ScreenCommand += c =>
        {
            if (c.Type != "disconnect")
                return;
            // The far end goes as the command is told, racing the link's own close: the connection may end cleanly or in an error.
            heldAtCommand = link.HeldFor;
            sw.Drop();
        };
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => heldAtCommand > TimeSpan.Zero), link.Words);
        // Recorded as the command was read, before the link closed its end or the far end dropped it.
        Assert.Equal(Options(sw).DisconnectHold, heldAtCommand);
        Assert.True(await Eventually.TrueAsync(() => link.Status != LinkStatus.Live), link.Words);
        await Task.Delay(3000);
        Assert.Equal((1, 1L), (sw.Sockets, link.Connections));
    }

    [Theory]
    [InlineData("", 60)]
    [InlineData(",\"retry\":30", 60)]
    [InlineData(",\"retry\":90", 90)]
    [InlineData(",\"retry\":99999", 3600)]
    public async Task ARefusalIsHeldForItsRetryAtLeastAMinuteAndAtMostAnHour(string retry, int seconds)
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = "{\"type\":\"auth\",\"ok\":false,\"error\":\"this key was revoked\"" + retry + "}";
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Refused), link.Words);
        Assert.Equal(TimeSpan.FromSeconds(seconds), link.RefusedFor);
    }

    [Fact]
    public async Task AKeyedCallRefusedWithRetryAfterIsHeldThatLong()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.ContextStatus = 401;
        sw.ContextAnswer = "{\"ok\":false,\"error\":\"too many wrong PINs — wait a minute\"}";
        sw.ContextRetryAfter = 120;
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Refused), link.Words);
        Assert.Equal((StagewatchLink.KeyRefused + "too many wrong PINs — wait a minute", TimeSpan.FromSeconds(120)), (link.Refusal, link.RefusedFor));
    }

    [Fact]
    public async Task ARateLimitHoldsKeyedCallsForItsRetryAfterAndRefusesNothing()
    {
        // A 429 is a good key asked too often: the pairing stands, and the keyed calls wait what Stagewatch asked.
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = VerdictKey;
        sw.ContextStatus = 429;
        sw.ContextAnswer = "{\"ok\":false,\"error\":\"rate limit: too many requests with this key\"}";
        sw.ContextRetryAfter = 30;
        await using var link = new StagewatchLink(Options(sw, approvals: true), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.BusyFor > TimeSpan.Zero), link.Words);
        Assert.Equal(TimeSpan.FromSeconds(30), link.BusyFor);
        Assert.Equal((PairState.Paired, "", TimeSpan.Zero), (link.Pairing, link.Refusal, link.RefusedFor));
        Assert.Equal($"Stagewatch at 127.0.0.1:{sw.Port} rate-limits this node's key — next try in 30 s (rate limit: too many requests with this key)", link.Words);
        var posted = sw.Contexts.Count;
        var asked = sw.Asked.Count(a => a.StartsWith("proposals", StringComparison.Ordinal));
        var pings = Pings(sw);
        Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= pings + 3), $"{Pings(sw)} pings");
        Assert.Equal((posted, asked), (sw.Contexts.Count, sw.Asked.Count(a => a.StartsWith("proposals", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task AKeylessLinkAskedForTheViewingCodeIsRefusedInThoseWordsAndHeld()
    {
        // Stagewatch says why and closes: the refusal is held as any refused admission, so the link does not dial, get let in and get
        // shut again at the backoff.
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = "{\"type\":\"auth\",\"ok\":false,\"view\":true,\"error\":\"Enter the viewing code\"}";
        sw.CloseAfterVerdict = true;
        await using var link = new StagewatchLink(Options(sw) with { Key = "" }, new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        var moves = new ConcurrentQueue<PairState>();
        link.PairingChanged += moves.Enqueue;
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Refused), link.Words);
        Assert.Equal(("viewing code asked — Enter the viewing code", StagewatchLink.RefusedHold), (link.Refusal, link.RefusedFor));
        Assert.Equal($"Stagewatch at 127.0.0.1:{sw.Port} lets in only what gives its viewing code or a key — Enter the viewing code", link.Words);
        Assert.True(await Eventually.TrueAsync(() => link.Status != LinkStatus.Live), link.Words);
        await Task.Delay(3000);
        Assert.Equal((1, 1L), (sw.Sockets, link.Connections));
        // Never called needing no proof: its first message was the refusal.
        Assert.Equal([PairState.Refused], moves);
    }

    [Fact]
    public async Task AKeyedAnswerThatLandsAfterANewerRefusalPairsNothing()
    {
        // A Stagewatch that sends no verdict: the context post is in flight when its operator revokes the key, and its 2xx lands after.
        await using var sw = await FakeStagewatch.StartAsync();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sw.ContextAnswerWhen = answer.Task;
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => !sw.Contexts.IsEmpty), link.Words);
        Assert.Equal(PairState.Unknown, link.Pairing);
        await sw.SendAsync("{\"type\":\"auth\",\"ok\":false,\"error\":\"this key was revoked\",\"retry\":60}");
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Refused), link.Words);
        var pings = Pings(sw);
        answer.SetResult();
        // The beat waits on the post: its pings going again say the late answer was read.
        Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= pings + 2), $"{Pings(sw)} pings");
        Assert.Equal((PairState.Refused, StagewatchLink.KeyRefused + "this key was revoked", StagewatchLink.RefusedHold),
            (link.Pairing, link.Refusal, link.RefusedFor));
        Assert.Single(sw.Contexts);
    }

    [Fact]
    public async Task ApprovedLinesAreCollectedOnlyOnceStagewatchTookTheKey()
    {
        // A Stagewatch that sends no verdict and fails the context post: the key is not yet taken, so no approved line is collected
        // (each runs as a paired session's); its first 2xx pairs the link, and the queue is asked.
        await using var sw = await FakeStagewatch.StartAsync();
        sw.ContextStatus = 500;
        await using var link = new StagewatchLink(Options(sw, approvals: true), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => sw.Contexts.Count >= 3), link.Words);
        Assert.Equal(PairState.Unknown, link.Pairing);
        Assert.Equal($"Stagewatch at 127.0.0.1:{sw.Port} refused this node's context — HTTP 500", link.Words);
        Assert.DoesNotContain(sw.Asked, a => a.StartsWith("proposals", StringComparison.Ordinal));
        sw.ContextStatus = 204;
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired), link.Words);
        Assert.True(await Eventually.TrueAsync(() => sw.Asked.Any(a => a.StartsWith("proposals", StringComparison.Ordinal))), link.Words);
    }

    [Fact]
    public async Task ARefusalThatHoldsNothingIsNotTheNewsOnceItsConnectionDrops()
    {
        // Stagewatch takes the key for no node and holds nothing (no retry, and the context answered with verified none): when the
        // connection then drops, what the link says as it goes down is the drop, not the refusal.
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = "{\"type\":\"auth\",\"ok\":true,\"scope\":\"node\",\"node\":{\"t\":\"playmap\",\"id\":\"a1b2c3d4\",\"verified\":\"none\"},\"words\":\"" + SpeaksForPatterns + "\"}";
        sw.ContextStatus = 200;
        sw.ContextAnswer = "{\"ok\":true,\"verified\":\"none\"}";
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        var down = new ConcurrentQueue<string>();
        link.StatusChanged += status =>
        {
            if (status == LinkStatus.Down)
                down.Enqueue(link.Words);
        };
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Refused && !sw.Contexts.IsEmpty), link.Words);
        Assert.Equal(TimeSpan.Zero, link.RefusedFor);
        sw.Drop();
        Assert.True(await Eventually.TrueAsync(() => !down.IsEmpty), link.Words);
        var said = down.First();
        Assert.StartsWith($"Stagewatch at 127.0.0.1:{sw.Port}: ", said, StringComparison.Ordinal);
        Assert.DoesNotContain(SpeaksForPatterns, said, StringComparison.Ordinal);
        Assert.DoesNotContain("unverified", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PastItsHoldARefusalStandsNoLongerAndTheDialSaysWhatItMet()
    {
        // The key refused and the socket gone; then Stagewatch itself goes. Once the hold ends on the link's clock, the refusal is not
        // the news: the link dials, and says what its dial met.
        var mono = new ManualClock(1_000_000_000);
        var sw = await FakeStagewatch.StartAsync();
        try
        {
            sw.Verdict = Unknown;
            await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), mono);
            var moves = new ConcurrentQueue<PairState>();
            link.PairingChanged += moves.Enqueue;
            link.Start();
            Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Refused), link.Words);
            sw.Drop();
            Assert.True(await Eventually.TrueAsync(() => link.Status != LinkStatus.Live), link.Words);
            Assert.Equal((PairState.Refused, StagewatchLink.RefusedHold), (link.Pairing, link.RefusedFor));
            Assert.StartsWith($"Stagewatch at 127.0.0.1:{sw.Port} refused this node's key", link.Words, StringComparison.Ordinal);
            var port = sw.Port;
            await sw.DisposeAsync();

            mono.Advance((long)StagewatchLink.RefusedHold.TotalMilliseconds * 1_000_000 + 1);
            Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Unknown
                && link.Words.StartsWith($"Stagewatch at 127.0.0.1:{port}: ", StringComparison.Ordinal), 15_000), link.Words);
            Assert.Equal(("", TimeSpan.Zero), (link.Refusal, link.RefusedFor));
            Assert.NotEqual(LinkStatus.Live, link.Status);
            Assert.Equal(1, link.Connections);
            Assert.Equal([PairState.Refused, PairState.Unknown], moves);
        }
        finally
        {
            await sw.DisposeAsync();
        }
    }

    /// <summary>Show Bus 1.3 §13.10.5: a beat that says the show is not live makes it stuck, and only the next state frame makes it live
    /// again — a plain beat, one that says live, or a pong never clears it.</summary>
    [Fact]
    public async Task OnlyAStateFrameClearsAStuckShowAPlainBeatOrAPongNever()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = VerdictKey;
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        await sw.SendAsync(Frame5);
        Assert.True(await Eventually.TrueAsync(() => link.Reading.Trusted), link.Words);
        await sw.SendAsync("{\"type\":\"beat\",\"live\":false}");
        Assert.True(await Eventually.TrueAsync(() => link.Stuck));

        await sw.SendAsync("{\"type\":\"beat\"}");
        await sw.SendAsync("{\"type\":\"beat\",\"live\":true}");
        // Two more pings answered after the beats, and time for their pongs to be read: neither beat nor pong cleared it.
        var pings = Pings(sw);
        Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= pings + 2), $"{Pings(sw) - pings} pings");
        await Task.Delay(300);
        Assert.True(link.Clock.Samples > 0);
        Assert.True(link.Stuck);
        Assert.Equal(("?", false), (link.Reading.Text, link.Reading.Trusted));

        await sw.SendAsync(Frame5.Replace("\"seq\":5", "\"seq\":6", StringComparison.Ordinal));
        Assert.True(await Eventually.TrueAsync(() => !link.Stuck && link.Reading.Trusted));
    }

    /// <summary>Show Bus 1.3 §13.7.2: the side calls run on a lane of their own, never in the beat's path. A context post Stagewatch holds
    /// unanswered never holds the pings or the liveness check: they go on every beat while it waits.</summary>
    [Fact]
    public async Task ASideCallStagewatchHoldsNeverHoldsTheBeat()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = VerdictKey;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sw.ContextAnswerWhen = held.Task;
        try
        {
            await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
            link.Start();
            Assert.True(await Eventually.TrueAsync(() => !sw.Contexts.IsEmpty), link.Words);
            var pings = Pings(sw);
            // The post waits (up to its own 4 s deadline); a beat every 200 ms goes on beside it.
            Assert.True(await Eventually.TrueAsync(() => Pings(sw) >= pings + 5, 2_500), $"{Pings(sw) - pings} pings while the post waited");
            Assert.Single(sw.Contexts);
            Assert.Equal(LinkStatus.Live, link.Status);
        }
        finally
        {
            held.TrySetResult();
        }
    }

    /// <summary>Show Bus 1.3 §13.12.9: the result of an approved line is owed until Stagewatch takes it — posted once it ran, and again on
    /// the schedule (3 s, doubling to 30 s, ±20 %, on the link's clock) after an answer that is not one, a 5xx or a 408 (a timeout,
    /// 13.12.3) — and the line runs once, however often Stagewatch still lists it. A 409 (settled already) counts as taken. A proposal is
    /// remembered by its desk, id and creation, so one a restarted desk numbers alike is its own.</summary>
    [Fact]
    public async Task AResultIsPostedAgainUntilStagewatchTakesItAndTheLineRunsOnce()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = VerdictKey;
        sw.ApprovedJson = "{\"proposals\":[{\"id\":\"p17\",\"target\":{\"t\":\"playmap\",\"id\":\"a1b2c3d4\"},\"line\":\"BLACKOUT ON\",\"state\":\"approved\",\"created\":1761500000000}]}";
        sw.ResultStatuses.Enqueue(503);
        sw.ResultStatuses.Enqueue(408);
        sw.ResultStatuses.Enqueue(409);
        var endpoint = new FakeEndpoint();
        var mono = new ManualClock(1_000_000_000);
        await using var link = new StagewatchLink(Options(sw, approvals: true), endpoint, TestSettings.Wall(), new PinStore(), mono);
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => !sw.Results.IsEmpty), link.Words);
        await Task.Delay(600);   // several more collects and turns of the lane: the line ran once, and the result waits for its time
        Assert.Equal((1L, 1, 1, 0L), (link.ApprovalsRun, sw.Results.Count, link.ResultsOwed, link.ResultsDelivered));

        // The first try again is 2.4–3.6 s on: not before. Its 408 is tried again too, 4.8–7.2 s on.
        mono.AdvanceMs(2_399);
        await Task.Delay(500);
        Assert.Single(sw.Results);
        mono.AdvanceMs(1_202);
        Assert.True(await Eventually.TrueAsync(() => sw.Results.Count == 2), $"{sw.Results.Count} posts, {link.ResultsOwed} owed");
        await Task.Delay(300);
        Assert.Equal((2, 1, 0L), (sw.Results.Count, link.ResultsOwed, link.ResultsDelivered));
        mono.AdvanceMs(4_799);
        await Task.Delay(500);
        Assert.Equal(2, sw.Results.Count);
        mono.AdvanceMs(2_402);
        Assert.True(await Eventually.TrueAsync(() => link.ResultsDelivered == 1), $"{sw.Results.Count} posts, {link.ResultsOwed} owed");
        Assert.Equal(3, sw.Results.Count);
        Assert.All(sw.Results, r => Assert.Equal(("p17", "{\"ok\":true,\"words\":\"blackout on\"}"), r));
        Assert.Equal((0, 1L), (link.ResultsOwed, link.ApprovalsRun));
        Assert.Single(endpoint.Lines);

        // The same id from a desk that started again, made at another time, is another proposal.
        sw.ApprovedJson = sw.ApprovedJson.Replace("1761500000000", "1761500099000", StringComparison.Ordinal);
        Assert.True(await Eventually.TrueAsync(() => link.ApprovalsRun == 2 && link.ResultsDelivered == 2), $"{link.ApprovalsRun} run");
    }

    /// <summary>Show Bus 1.3 §13.10.2: a pong is a clock sample only when it answers one of the last pings; one for a ping never sent is
    /// counted and gives no sample.</summary>
    [Fact]
    public async Task APongIsAClockSampleOnlyWhenItAnswersOneOfTheLastPings()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.ForgedPongs = true;
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.UnmatchedPongs >= 3), link.Words);
        Assert.Equal(0, link.Clock.Samples);
        sw.ForgedPongs = false;
        Assert.True(await Eventually.TrueAsync(() => link.Clock.Samples > 0), link.Words);
    }

    /// <summary>Show Bus 1.3 §13.15: the backoff starts over only after a connection Stagewatch settled stayed up 10 s on the link's
    /// clock. One taken and closed at once — a socket that answers and closes — waits longer at each dial; after a stable one, a dial
    /// that fails anywhere (here its upgrade, refused) counts again from the first wait and doubles the next. Each wait is read as the
    /// link computes it, so real time decides nothing here.</summary>
    [Fact]
    public async Task ARedialWaitsLongerForEachDialThatDidNotStayUpUntilOneStaysTenSeconds()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = VerdictKey;
        sw.CloseAfterVerdict = true;
        var mono = new ManualClock(1_000_000_000);
        var waits = new ConcurrentQueue<(int N, TimeSpan Wait)>();
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore(), mono);
        link.Redialling += (n, wait) => waits.Enqueue((n, wait));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => waits.Count >= 3, 10_000), $"{waits.Count} redials: {link.Words}");
        Assert.Equal(new[] { 0, 1, 2 }, waits.Take(3).Select(w => w.N));
        Assert.All(waits, w => InRedialWait(w.N, w.Wait));

        sw.CloseAfterVerdict = false;
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired && link.Status == LinkStatus.Live, 15_000), link.Words);
        mono.AdvanceMs(9_999);
        await Task.Delay(400);
        Assert.False(link.Stable);
        mono.AdvanceMs(1);
        Assert.True(await Eventually.TrueAsync(() => link.Stable), "the settled connection was never marked stable");

        // The stable connection drops, and every dial after it fails at its upgrade: 400–600 ms, then 800–1200, then 1600–2400.
        var before = waits.Count;
        sw.RefuseSockets = true;
        sw.Drop();
        Assert.True(await Eventually.TrueAsync(() => waits.Count >= before + 3, 15_000), $"{waits.Count - before} redials: {link.Words}");
        var after = waits.Skip(before).Take(3).ToList();
        Assert.Equal(new[] { 0, 1, 2 }, after.Select(w => w.N));
        Assert.All(after, w => InRedialWait(w.N, w.Wait));
        Assert.False(link.Stable);
    }

    /// <summary>13.15.1's wait for redial <paramref name="n"/>, ±20 %.</summary>
    internal static void InRedialWait(int n, TimeSpan wait) =>
        Assert.InRange(wait.TotalMilliseconds, PeerLink.RedialWait(n, 0), PeerLink.RedialWait(n, 0.9999999));

    /// <summary>Show Bus 1.3 §13.7.2: a side call's fault is its own. A node whose context faults each time it is read still collects
    /// what its approval desk approved, runs it and posts its result, the link live throughout, and each fault is counted on the
    /// context's item alone. A line whose command path faults as it runs is said as one that may have run: its result is owed and
    /// delivered as any other, and the line is never run again.</summary>
    [Fact]
    public async Task ASideCallsFaultIsItsOwnAndALineThatFaultsAsItRunsIsSaidAsOneThatMayHaveRun()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        sw.Verdict = VerdictKey;
        sw.ApprovedJson = "{\"proposals\":[{\"id\":\"p21\",\"target\":{\"t\":\"playmap\",\"id\":\"a1b2c3d4\"},\"line\":\"STATE\",\"state\":\"approved\",\"created\":1761500000000}]}";
        var endpoint = new FakeEndpoint { ThrowOnContext = true, ThrowOnState = true };
        await using var link = new StagewatchLink(Options(sw, approvals: true), endpoint, TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.ResultsDelivered == 1),
            $"{sw.Results.Count} results, {link.SideCallFaults(StagewatchSideCall.Context)} context faults: {link.Words}");
        Assert.True(await Eventually.TrueAsync(() => link.SideCallFaults(StagewatchSideCall.Context) >= 2), "the context was read once");
        await Task.Delay(400);   // more turns of the lane: the line is still listed, and never run again
        var (id, body) = Assert.Single(sw.Results);
        using var result = JsonDocument.Parse(body);
        Assert.Equal(("p21", false, StagewatchLink.FaultedAsItRan),
            (id, result.RootElement.GetProperty("ok").GetBoolean(), result.RootElement.GetProperty("words").GetString()));
        Assert.Equal((0L, 0L), (link.SideCallFaults(StagewatchSideCall.Approvals), link.SideCallFaults(StagewatchSideCall.Results)));
        Assert.Empty(sw.Contexts);
        Assert.Equal((LinkStatus.Live, 0, 1L), (link.Status, link.ResultsOwed, link.Connections));
    }

    /// <summary>The numbers the link keeps for §13's rows the core does not (PeerLinkVectorsTests names them): each side call's
    /// deadline (<c>link.sidecall.timeout.ms</c>) and the operator's disconnect hold (<c>peerlink.hold.disconnect.ms</c>).</summary>
    [Fact]
    public void TheLinksOwnNumbersAreSection14s()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "SHOWBUS.md"));
        long Row(string name) => long.Parse(text.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith(name + " ", StringComparison.Ordinal))[(name.Length + 1)..],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Row("link.sidecall.timeout.ms"), (long)StagewatchLink.SideCallWithin.TotalMilliseconds);
        Assert.Equal(1, Row("link.sidecall.inflight"));
        Assert.Equal(Row("peerlink.hold.disconnect.ms"), (long)new StagewatchLinkOptions { Host = "h", SelfId = "a1b2c3d4" }.DisconnectHold.TotalMilliseconds);
        Assert.Equal(Row("peerlink.stable.ms"), (long)new StagewatchLinkOptions { Host = "h", SelfId = "a1b2c3d4" }.StableAfter.TotalMilliseconds);
    }

    [Fact]
    public void AFrameCountsOnFromItsArrivalAndFormatsAsStagewatchDoes()
    {
        using var doc = JsonDocument.Parse(Frame5);
        var t = StagewatchTimer.Parse(doc.RootElement)!;
        Assert.Equal("12:30", t.CountedOn(0.5).Text);
        Assert.Equal("12:29", t.CountedOn(1.5).Text);
        Assert.Equal("00:00", t.CountedOn(750).Text);
        Assert.Equal("-00:05", t.CountedOn(755.2).Text);
        Assert.Equal("1:02:03", StagewatchTimer.Format(3723));
        Assert.Equal(750, (t with { State = "paused" }).CountedOn(10).Seconds);
        Assert.Null(StagewatchTimer.Parse(JsonDocument.Parse("{\"type\":\"beat\"}").RootElement));
    }
}
