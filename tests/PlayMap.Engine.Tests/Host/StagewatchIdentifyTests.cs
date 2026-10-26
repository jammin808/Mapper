using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PlayMap.Net.Links;

namespace PlayMap.Engine.Tests.Host;

/// <summary>A Stagewatch's Screens socket played small: <c>/ws</c> takes a screen, counts it and keeps the Authorization it came with,
/// sends it its <see cref="Verdict"/> first when one is set, then what the test queues, and answers its pings with pongs, so the link
/// stays live until the test <see cref="Drop"/>s it. A message is read whole, however many frames it arrives in, before it is
/// parsed.</summary>
internal sealed class ScreensStagewatch : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Channel<string> _outbox = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<HttpContext, bool> _open = new();
    private int _screens;
    private int _stopped;

    private ScreensStagewatch(WebApplication app) => _app = app;

    public int Port { get; private set; }

    /// <summary>Screens that have connected so far.</summary>
    public int Screens => Volatile.Read(ref _screens);

    /// <summary>Each screen's Authorization header, in the order they came ("" for none).</summary>
    public ConcurrentQueue<string> Authorizations { get; } = new();

    /// <summary>The first message each connection hears (Stagewatch 2.18's verdict on a node link), or null for none.</summary>
    public string? Verdict { get; set; }

    /// <summary>Every connection open now goes, with no close.</summary>
    public void Drop()
    {
        foreach (var c in _open.Keys)
            c.Abort();
    }

    public static async Task<ScreensStagewatch> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        var app = builder.Build();
        var fake = new ScreensStagewatch(app);
        app.UseWebSockets();
        app.Map("/ws", fake.WsAsync);
        await app.StartAsync();
        fake.Port = new Uri(app.Urls.First()).Port;
        return fake;
    }

    public ValueTask SendAsync(string json) => _outbox.Writer.WriteAsync(json);

    private async Task WsAsync(HttpContext c)
    {
        Authorizations.Enqueue(c.Request.Headers.Authorization.ToString());
        using var ws = await c.WebSockets.AcceptWebSocketAsync();
        Interlocked.Increment(ref _screens);
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
            await ws.SendAsync(Encoding.UTF8.GetBytes(verdict), WebSocketMessageType.Text, true, c.RequestAborted);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted);
        var sender = Task.Run(async () =>
        {
            await foreach (var m in _outbox.Reader.ReadAllAsync(stop.Token))
                await ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, stop.Token);
        }, CancellationToken.None);
        var buffer = new byte[8192];
        var message = new ArrayBufferWriter<byte>();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, stop.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
                message.Write(buffer.AsSpan(0, r.Count));
                if (!r.EndOfMessage)
                    continue;
                using var doc = JsonDocument.Parse(message.WrittenMemory);
                message.ResetWrittenCount();
                if (doc.RootElement.TryGetProperty("cmd", out var cmd) && cmd.GetString() == "ping")
                {
                    var t = doc.RootElement.GetProperty("t").GetInt64();
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await _outbox.Writer.WriteAsync(string.Create(CultureInfo.InvariantCulture, $"{{\"type\":\"pong\",\"t\":{t},\"t2\":{now},\"t3\":{now}}}"), stop.Token);
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

/// <summary>Stagewatch's "identify" (its Screens page, often "identify all") on a node that follows it as a screen: one journal
/// row that says nothing flashed and names Stagewatch as who asked, and no OUTPUT IDENTIFY line reaches the engine — rehearsing
/// (where the line would flash every output) or armed (where it would be a refusal in the journal) — while the air's identify stays
/// off and the link stays live.</summary>
public sealed class StagewatchIdentifyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagewatchsIdentifyIsOneJournalRowAndNoOutputIdentifyReachesTheEngine(bool armed)
    {
        await using var sw = await ScreensStagewatch.StartAsync();
        var address = "127.0.0.1:" + sw.Port.ToString(CultureInfo.InvariantCulture);
        await using var rig = await HostRig.StartAsync("Stage left", settings: s => s with { StagewatchEnabled = true, StagewatchHost = address });
        Assert.True(await Eventually.TrueAsync(() => rig.Network.Stagewatch()?.State == "live" && sw.Screens == 1), rig.LogTail());
        if (armed)
            await rig.OkAsync("SHOW ARM");
        Assert.Equal(armed, rig.State().GetProperty("show").GetProperty("armed").GetBoolean());
        var rows = new ConcurrentQueue<JournalRow>();
        rig.Engine.Journal.Written += rows.Enqueue;

        await sw.SendAsync("{\"type\":\"identify\",\"name\":\"Stage left\",\"id\":\"" + rig.Id + "\"}");
        await sw.SendAsync("{\"type\":\"reload\"}");
        // The link reads Stagewatch's messages in order on one reader, so the reload's row lands after whatever the identify did.
        Assert.True(await Eventually.TrueAsync(() => rows.Any(r => r.Text.Contains("to reload", StringComparison.Ordinal))), rig.Journal());
        var reload = Assert.Single(rows, r => r.Text.Contains("to reload", StringComparison.Ordinal));
        // A line the desk sends now takes its turn on the control lane after any line the identify had put there.
        await rig.RunAsync("OUTPUT LIST");

        var identify = Assert.Single(rows, r => r.Text == StagewatchScreenCommands.IdentifyRow);
        Assert.Equal(("network", true, "Stagewatch", (string?)null), (identify.Kind, identify.Ok, identify.Who, identify.Line));
        Assert.Equal(("network", "Stagewatch"), (reload.Kind, reload.Who));
        Assert.DoesNotContain(rows, r => r.Line?.StartsWith("OUTPUT IDENTIFY", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(rows, r => r.Text.Contains("identifying every output", StringComparison.Ordinal));
        Assert.False(rig.State().GetProperty("air").GetProperty("identify").GetBoolean());
        Assert.Equal(armed, rig.State().GetProperty("show").GetProperty("armed").GetBoolean());
        Assert.Equal(("live", ""), (rig.Network.Stagewatch()!.State, rig.Network.StagewatchView));
        Assert.Equal(1, sw.Screens);
    }
}
