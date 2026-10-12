using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Wire;
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

    public static async Task<FakeStagewatch> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
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

/// <summary>PlayMap as a Stagewatch screen: asked for with its cid, page and the key in a header (never the URL),
/// frames counted on locally and taken latest-wins, "?" while Stagewatch says it is stuck, the operator's redirect
/// raised, pings closing a clock exchange, the context posted, and approved lines collected, run once through the
/// node's own path and answered.</summary>
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
        await using var link = new StagewatchLink(Options(sw), new FakeEndpoint(), TestSettings.Wall(), new PinStore());
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
        await Task.Delay(1100);
        Assert.InRange(link.Reading.Timer!.Seconds, 740, 749);
        Assert.Equal("12:29", link.Reading.Text);

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
