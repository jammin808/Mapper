using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Companion;

/// <summary>Companion's door on a real node: the line channel on the web port pairs with the node's token and runs PLAYLIST GO; before
/// it pairs an edit is refused and nothing moves; its HELLO name makes it a companion in STATE.clients and WHOAMI, never more; and the
/// web port serves THUMB's pictures with their ETag.</summary>
public sealed class CompanionWireTests
{
    private sealed class Lines(ClientWebSocket socket) : IAsyncDisposable
    {
        private readonly byte[] _buffer = new byte[1 << 20];

        public static async Task<Lines> ConnectAsync(int port)
        {
            var ws = new ClientWebSocket();
            ws.Options.AddSubProtocol("playmap.lines.v1");
            await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/ws"), CancellationToken.None);
            return new Lines(ws);
        }

        public Task SendAsync(string line) => socket.SendAsync(Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<string> ReadAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var count = 0;
            while (true)
            {
                var r = await socket.ReceiveAsync(_buffer.AsMemory(count), deadline.Token);
                Assert.NotEqual(WebSocketMessageType.Close, r.MessageType);
                count += r.Count;
                if (r.EndOfMessage)
                    return Encoding.UTF8.GetString(_buffer, 0, count);
            }
        }

        /// <summary>The reply to a line with this id (pushes and nonces on the way are passed over).</summary>
        public async Task<string> AskAsync(string id, string line)
        {
            await SendAsync("@" + id + " " + line);
            while (true)
            {
                var heard = await ReadAsync();
                if (heard.StartsWith("OK @" + id, StringComparison.Ordinal) || heard.StartsWith("ERR @" + id, StringComparison.Ordinal))
                    return heard;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            socket.Dispose();
        }
    }

    [Fact]
    public async Task ACompanionPairsOnTheWebPortAndRunsPlaylistGo()
    {
        await using var rig = await HostRig.StartAsync(settings: s => s with { TrustLoopback = false });
        var list = await rig.OkAsync("PLAYLIST NEW Show");
        await rig.OkAsync($"PLAYLIST ADD {list} color:#FF0000 Red");

        await using var ws = await Lines.ConnectAsync(rig.Network.HttpPort);
        var nonce = await ws.ReadAsync();
        Assert.StartsWith("NONCE ", nonce, StringComparison.Ordinal);
        Assert.StartsWith("OK @h", await ws.AskAsync("h", "HELLO Companion:FOH deck"), StringComparison.Ordinal);
        Assert.StartsWith("ERR @g0 not paired", await ws.AskAsync("g0", $"PLAYLIST GO {list}"), StringComparison.Ordinal);
        Assert.Equal("idle", rig.State().GetProperty("playlists")[0].GetProperty("state").GetString());

        Assert.Equal("OK @p paired", await ws.AskAsync("p", "AUTH-PROOF " + Pairing.Proof(HostRig.Token, nonce[6..], "Companion:FOH deck")));
        var go = await ws.AskAsync("g1", $"PLAYLIST GO {list}");
        Assert.StartsWith("OK @g1", go, StringComparison.Ordinal);
        Assert.Equal("playing", rig.State().GetProperty("playlists")[0].GetProperty("state").GetString());

        var me = JsonDocument.Parse((await ws.AskAsync("w", "WHOAMI"))["OK @w ".Length..]).RootElement;
        Assert.Equal("companion", me.GetProperty("transport").GetString());
        Assert.Equal("ws", me.GetProperty("channel").GetString());
        Assert.Equal("remote", me.GetProperty("role").GetString());
        var client = rig.State().GetProperty("clients").EnumerateArray().Single();
        Assert.Equal("Companion:FOH deck", client.GetProperty("name").GetString());
        Assert.Equal("ws", client.GetProperty("channel").GetString());
        Assert.True(client.GetProperty("paired").GetBoolean());
    }

    [Fact]
    public async Task OnAnArmedNodeWhoAmIListsWhatItMayRunAndARefusalReachesTheDeckWithWords()
    {
        await using var rig = await HostRig.StartAsync();
        var list = await rig.OkAsync("PLAYLIST NEW Show");
        await rig.OkAsync($"PLAYLIST ADD {list} color:#FF0000 Red");
        await rig.OkAsync("SHOW ARM");

        await using var ws = await Lines.ConnectAsync(rig.Network.HttpPort);
        Assert.StartsWith("OK @h", await ws.AskAsync("h", "HELLO Companion:FOH deck"), StringComparison.Ordinal);
        var me = JsonDocument.Parse((await ws.AskAsync("w", "WHOAMI"))["OK @w ".Length..]).RootElement;
        Assert.True(me.GetProperty("armed").GetBoolean());
        Assert.Equal(JsonValueKind.Array, me.GetProperty("may").ValueKind);
        var may = me.GetProperty("may").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains("WHOAMI", may);
        Assert.DoesNotContain("PLAYLIST NEW", may);

        var refused = await ws.AskAsync("e", "PLAYLIST NEW Encore");
        Assert.StartsWith("ERR @e ", refused, StringComparison.Ordinal);
        Assert.True(refused.Length > "ERR @e ".Length + 10, refused);
        Assert.Single(rig.State().GetProperty("playlists").EnumerateArray());
    }

    [Fact]
    public async Task TheWebPortServesAPictureWithItsEtag()
    {
        await using var rig = await HostRig.StartAsync();
        using var http = rig.Http();
        using var first = await http.GetAsync("/api/thumb?ref=" + Uri.EscapeDataString("color:#00FF00") + "&w=16&h=16&fmt=png");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        var etag = first.Headers.ETag!;
        Assert.Equal("\"color-00FF00-16x16-p\"", etag.Tag);
        Assert.NotEmpty(await first.Content.ReadAsByteArrayAsync());

        using var again = new HttpRequestMessage(HttpMethod.Get, "/api/thumb?ref=" + Uri.EscapeDataString("color:#00FF00") + "&w=16&h=16&fmt=png");
        again.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag.Tag));
        using var same = await http.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);

        using var none = await http.GetAsync("/api/thumb?ref=media:md00000042");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Contains("no media md00000042", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
