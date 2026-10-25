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

/// <summary>A Stagewatch as the tests play it: /ws records how it was asked (path, query, Authorization) and sends what
/// the test queues, answering pings with pongs; /api/peers/context records the posted context; /api/proposals serves
/// the approved list for a target; /api/proposals/&lt;id&gt;/result records each answer.</summary>
internal sealed class FakeStagewatch : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Channel<string> _outbox = Channel.CreateUnbounded<string>();

    private FakeStagewatch(WebApplication app) => _app = app;

    public int Port { get; private set; }

    public ConcurrentQueue<string> Asked { get; } = new();

    public ConcurrentQueue<string> Contexts { get; } = new();

    public ConcurrentQueue<(string Id, string Body)> Results { get; } = new();

    public ConcurrentQueue<string> Received { get; } = new();

    public string ApprovedJson { get; set; } = "{\"proposals\":[]}";

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
            using var r = new StreamReader(c.Request.Body);
            fake.Contexts.Enqueue(c.Request.Headers.Authorization + " " + await r.ReadToEndAsync());
            c.Response.StatusCode = 204;
        });
        app.MapGet("/api/proposals", (HttpContext c) =>
        {
            fake.Asked.Enqueue("proposals " + c.Request.QueryString.Value);
            return Microsoft.AspNetCore.Http.Results.Text(fake.ApprovedJson, "application/json");
        });
        app.MapPost("/api/proposals/{id}/result", async (HttpContext c) =>
        {
            using var r = new StreamReader(c.Request.Body);
            fake.Results.Enqueue(((string)c.Request.RouteValues["id"]!, await r.ReadToEndAsync()));
            return Microsoft.AspNetCore.Http.Results.Text("{\"ok\":true}", "application/json");
        });
        await app.StartAsync();
        fake.Port = new Uri(app.Urls.First()).Port;
        return fake;
    }

    public ValueTask SendAsync(string json) => _outbox.Writer.WriteAsync(json);

    private async Task WsAsync(HttpContext c)
    {
        Asked.Enqueue("ws " + c.Request.Path + c.Request.QueryString.Value + " auth=" + c.Request.Headers.Authorization);
        using var ws = await c.WebSockets.AcceptWebSocketAsync();
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
                    var t = doc.RootElement.GetProperty("t").GetInt64();
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

    public async ValueTask DisposeAsync()
    {
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

/// <summary>PlayMap as a Stagewatch screen: asked for with its cid, page and the key in a header (never the URL),
/// frames counted on locally and taken latest-wins, "?" while Stagewatch says it is stuck, the operator's redirect
/// raised, pings closing a clock exchange, the context posted, and approved lines collected, run once through the
/// node's own path and answered. Every dial tries TLS first; a pinned certificate, or one the hello names, is TLS or nothing; over
/// plain ws the key goes only to this machine, and off it the timer is read without the key.</summary>
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
        Assert.Equal("ws /ws?cid=a1b2c3d4&page=/playmap&detail=1 auth=Bearer swk_test0123456789", asked);

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
            Assert.Equal("ws /ws?cid=a1b2c3d4&page=/playmap&detail=1 auth=Bearer swk_test0123456789", Assert.Single(sw.Asked));
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
        Assert.Equal("ws /ws?cid=a1b2c3d4&page=/playmap&detail=1 auth=", Assert.Single(sw.Asked));
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
            Assert.Equal("ws /ws?cid=a1b2c3d4&page=/playmap&detail=1 auth=Bearer swk_test0123456789", Assert.Single(sw.Asked));
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
        Assert.Equal("ws /ws?cid=a1b2c3d4&page=/playmap&detail=1 auth=" + (local ? "Bearer swk_test0123456789" : ""), Assert.Single(sw.Asked));
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
            Assert.Equal("ws /ws?cid=a1b2c3d4&page=/playmap&detail=1 auth=", Assert.Single(sw.Asked));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static int Pings(FakeStagewatch sw) => sw.Received.Count(m => m.Contains("\"cmd\":\"ping\"", StringComparison.Ordinal));

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
