using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Phone;
using PlayMap.Core.Time;
using PlayMap.Engine.Host;
using PlayMap.Engine.Phone;
using PlayMap.Engine.Tests.Host;
using PlayMap.Tests;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>A node's phone hub runs on the node's clocks (<see cref="EngineHostOptions.Time"/> and <see cref="EngineHostOptions.Mono"/>):
/// an invite expires on the node's time, a socket that says no hello is told so once the node's time passes the hello's wait, and the
/// clock exchange answers on the node's monotonic clock, the clock presents are stamped on.</summary>
public sealed class PhoneNodeClockTests
{
    [Fact]
    public async Task TheNodesPhoneHubRunsOnTheNodesClocks()
    {
        var time = new ManualTime(new DateTimeOffset(2031, 5, 4, 3, 2, 1, TimeSpan.Zero));
        var mono = new ManualClock(7_000_000_000);
        await using var node = await HostRig.StartAsync("Phone node", mono: mono, time: time,
            parts: EngineParts.Bare with { Extensions = [PhoneExtension.Factory] });

        var invite = JsonDocument.Parse(await node.OkAsync("PHONE INVITE Pixel 8 FOR 5")).RootElement;
        Assert.Equal((time.GetUtcNow() + TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds(), invite.GetProperty("expiresUnixMs").GetInt64());
        var url = invite.GetProperty("url").GetString()!;
        var ticket = url[(url.IndexOf("#t=", StringComparison.Ordinal) + 3)..];

        // A socket that says nothing is told why once the node's time passes the hello's 10 s wait.
        var before = time.Waiting;
        using (var mute = await ConnectAsync(node))
        {
            Assert.True(await Eventually.TrueAsync(() => time.Waiting > before, 30_000), "the wait for the hello never stood on the node's time");
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Null(await ReceiveAsync(mute));
            Assert.Equal((WebSocketCloseStatus.PolicyViolation, "no hello"), (mute.CloseStatus, mute.CloseStatusDescription));
        }

        // A phone's clock exchange is answered on the node's monotonic clock.
        using var phone = await ConnectAsync(node);
        await SendAsync(phone, new
        {
            t = "hello",
            v = 1,
            ticket,
            ua = new { platform = "android-chrome", model = "Pixel 8" },
            camera = new { label = "camera2 0, facing back", width = 320, height = 240, fps = 30, caps = new { exposure = Array.Empty<string>(), exposureTime = Array.Empty<int>() } },
        });
        var welcome = await ReceiveAsync(phone);
        Assert.Equal("welcome", welcome?.GetProperty("t").GetString());
        await SendAsync(phone, new { t = "ping", t1 = 1.0 });
        JsonElement? pong;
        do
            pong = await ReceiveAsync(phone);
        while (pong is { } m && m.GetProperty("t").GetString() != "pong");
        Assert.NotNull(pong);
        Assert.Equal((mono.NowNs / 1e6, mono.NowNs / 1e6), (pong.Value.GetProperty("t2").GetDouble(), pong.Value.GetProperty("t3").GetDouble()));
    }

    private static async Task<ClientWebSocket> ConnectAsync(HostRig node)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol(PhoneProtocol.SubProtocol);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ws.ConnectAsync(new Uri(string.Create(CultureInfo.InvariantCulture, $"ws://127.0.0.1:{node.Network.HttpPort}/scan/ws")), limit.Token);
        return ws;
    }

    private static async Task SendAsync(ClientWebSocket ws, object message) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The next text message, or null once the node has closed the socket.</summary>
    private static async Task<JsonElement?> ReceiveAsync(ClientWebSocket ws)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var buffer = new byte[64 * 1024];
        var length = 0;
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer.AsMemory(length), limit.Token);
            if (r.MessageType == WebSocketMessageType.Close)
                return null;
            length += r.Count;
            if (!r.EndOfMessage)
                continue;
            if (r.MessageType == WebSocketMessageType.Text)
            {
                using var doc = JsonDocument.Parse(buffer.AsMemory(0, length));
                return doc.RootElement.Clone();
            }
            length = 0;
        }
    }
}
