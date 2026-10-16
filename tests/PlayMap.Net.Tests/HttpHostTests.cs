using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PlayMap.Core.Bus;
using PlayMap.Net.Http;
using PlayMap.Net.Tls;

namespace PlayMap.Net.Tests;

/// <summary>The web port as a browser, Stagewatch-style client and another node meet it: the read routes, commands
/// with Show Bus receipts, the long-poll, the token gate (headers only, wrong tokens rate-limited, HTTPS required
/// from the network), the WebSocket's frames, beats, pongs and receipts, and plain HTTP and HTTPS on one port with
/// the node's own certificate.</summary>
public class HttpHostTests
{
    internal static async Task<HttpHost> StartAsync(FakeEndpoint endpoint, NodeSettings settings, HttpHostOptions? options = null)
    {
        var host = new HttpHost(settings, endpoint, TestSettings.Wall(), (options ?? new HttpHostOptions()) with { Port = 0 });
        await host.StartAsync();
        return host;
    }

    private static HttpClient Client(HttpHost host, string? token = null)
    {
        var c = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + host.Port), Timeout = TimeSpan.FromSeconds(30) };
        if (token is not null)
            c.DefaultRequestHeaders.Add("X-PlayMap-Token", token);
        return c;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostLineAsync(HttpClient c, string line, string? requestId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cmd") { Content = new StringContent(line, Encoding.UTF8, "text/plain") };
        if (requestId is not null)
            request.Headers.Add("X-Request-Id", requestId);
        using var response = await c.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return (response.StatusCode, doc.RootElement.Clone());
    }

    [Fact]
    public async Task TheReadRoutesAnswerStateContextProjectAndTheVocabulary()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        using var c = Client(host);
        Assert.StartsWith("{\"v\":1,\"seq\":1,\"id\":\"a1b2c3d4\"", await c.GetStringAsync("/api/state"), StringComparison.Ordinal);
        Assert.StartsWith("{\"v\":1,\"node\":", await c.GetStringAsync("/api/context"), StringComparison.Ordinal);
        Assert.Equal("{\"name\":\"Show\"}", await c.GetStringAsync("/api/project"));
        using var verbs = JsonDocument.Parse(await c.GetStringAsync("/api/verbs"));
        var blackout = verbs.RootElement.EnumerateArray().Single(v => v.GetProperty("name").GetString() == "BLACKOUT");
        Assert.Equal("high", blackout.GetProperty("risk").GetString());
        Assert.True(blackout.GetProperty("mutates").GetBoolean());
        using var missing = await c.GetAsync("/nowhere");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ACommandWithARequestIdAnswersAShowBusReceipt()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        using var c = Client(host);

        var (status, receipt) = await PostLineAsync(c, "BLACKOUT ON", "a1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("receipt", receipt.GetProperty("type").GetString());
        Assert.Equal("a1", receipt.GetProperty("id").GetString());
        Assert.True(receipt.GetProperty("ok").GetBoolean());
        Assert.Equal("applied", receipt.GetProperty("stage").GetString());
        Assert.Equal("blackout on", receipt.GetProperty("effect").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("error").ValueKind);
        Assert.True(endpoint.Blackout);
        Assert.Equal("http", Assert.Single(endpoint.Lines).Session.Transport);

        (status, receipt) = await PostLineAsync(c, "BLACKOUT ON", "a2");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.False(receipt.GetProperty("ok").GetBoolean());
        Assert.Equal("already blacked out", receipt.GetProperty("error").GetString());

        (_, receipt) = await PostLineAsync(c, "@s9 SCAN START cam1");
        Assert.Equal("s9", receipt.GetProperty("id").GetString());
        Assert.Equal("accepted", receipt.GetProperty("stage").GetString());
        Assert.Equal("scan started", receipt.GetProperty("effect").GetString());

        (_, receipt) = await PostLineAsync(c, "PLAYLIST NEW Show", "n1");
        Assert.Equal("pl_1", receipt.GetProperty("refs").GetProperty("reply").GetString());

        (status, receipt) = await PostLineAsync(c, "BLACKOUT OFF");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("OK", receipt.GetProperty("msg").GetString());

        (status, receipt) = await PostLineAsync(c, "HELLO Browser", "h1");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("line wire", receipt.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTokenRidesInAHeaderNeverAUrlAndWrongTokensLockTheAddress()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: false));
        using (var anonymous = Client(host))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/state")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/state?token=" + TestSettings.Token)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/tls")).StatusCode);
            var (status, _) = await PostLineAsync(anonymous, "BLACKOUT ON", "x");
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.False(endpoint.Blackout);
        }
        using (var paired = Client(host, "k7qm3xwdp9ra"))
            Assert.Equal(HttpStatusCode.OK, (await paired.GetAsync("/api/state")).StatusCode);
        using (var bearer = Client(host))
        {
            bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestSettings.Token);
            var (status, receipt) = await PostLineAsync(bearer, "BLACKOUT ON", "b");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(receipt.GetProperty("ok").GetBoolean());
        }
        using var wrong = Client(host, "WRONG-WRONG-1");
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await wrong.GetAsync("/api/state")).StatusCode);
        using var right = Client(host, TestSettings.Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await right.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public void RequireHttpsRefusesPlainFromTheNetworkButNotFromThisMachine()
    {
        var gate = new TokenGate();
        var settings = TestSettings.Make(trustLoopback: true) with { RequireHttps = true };
        var remote = new DefaultHttpContext();
        remote.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.9");
        Assert.Equal(GateVerdict.NeedsHttps, gate.Check(remote, settings));
        remote.Request.IsHttps = true;
        remote.Request.Headers["X-PlayMap-Token"] = TestSettings.Token;
        Assert.Equal(GateVerdict.Token, gate.Check(remote, settings));
        var local = new DefaultHttpContext();
        local.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.Equal(GateVerdict.Open, gate.Check(local, settings));
        var trusted = new DefaultHttpContext();
        trusted.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.7");
        trusted.Request.IsHttps = true;
        Assert.Equal(GateVerdict.Open, gate.Check(trusted, settings with { TrustedAddresses = ["10.0.0.7"] }));
    }

    [Fact]
    public async Task TheLongPollWaitsForTheNextChangeAndGivesUpAtItsLimit()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true), new HttpHostOptions { LongPollMax = TimeSpan.FromMilliseconds(600) });
        using var c = Client(host);
        var waiting = c.GetStringAsync("/api/state?since=1");
        await Task.Delay(150);
        Assert.False(waiting.IsCompleted);
        endpoint.Publish();
        Assert.Contains("\"seq\":2,", await waiting.WaitAsync(TimeSpan.FromSeconds(5)), StringComparison.Ordinal);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Contains("\"seq\":2,", await c.GetStringAsync("/api/state?since=2"), StringComparison.Ordinal);
        Assert.InRange(sw.ElapsedMilliseconds, 500, 5000);
        Assert.Contains("\"seq\":2,", await c.GetStringAsync("/api/state?since=1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWebSocketSendsStateFramesBeatsPongsAndReceipts()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true), new HttpHostOptions { BeatEvery = TimeSpan.FromMilliseconds(300) });
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + host.Port + "/ws"), CancellationToken.None);
        var first = await ReceiveAsync(ws);
        Assert.StartsWith("{\"type\":\"state\",\"v\":1,\"seq\":1,\"id\":\"a1b2c3d4\"", first, StringComparison.Ordinal);
        Assert.Equal("{\"type\":\"beat\"}", await ReceiveAsync(ws));

        await SendAsync(ws, "{\"cmd\":\"ping\",\"t\":1760101234567}");
        var pong = await ReceiveUntilAsync(ws, m => m.Contains("\"pong\"", StringComparison.Ordinal));
        using (var p = JsonDocument.Parse(pong))
        {
            Assert.Equal(1760101234567, p.RootElement.GetProperty("t").GetInt64());
            Assert.True(p.RootElement.GetProperty("t3").GetInt64() >= p.RootElement.GetProperty("t2").GetInt64());
        }

        await SendAsync(ws, "{\"cmd\":\"BLACKOUT ON\",\"id\":\"a1\"}");
        var (receipt, frame) = await ReceiptAndFrameAsync(ws, "a1", 2);
        Assert.StartsWith("{\"type\":\"receipt\",\"id\":\"a1\",\"ok\":true,\"stage\":\"applied\",\"effect\":\"blackout on\"", receipt, StringComparison.Ordinal);
        Assert.Contains("\"blackout\":true", frame, StringComparison.Ordinal);
        Assert.Equal("ws", Assert.Single(endpoint.Lines).Session.Transport);

        endpoint.IsLive = false;
        Assert.Equal("{\"type\":\"beat\",\"live\":false}", await ReceiveUntilAsync(ws, m => m.Contains("\"live\":false", StringComparison.Ordinal)));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task AWebSocketCommandsArgumentsAreWordsAndItsLastJsonRidesBare()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true), new HttpHostOptions { BeatEvery = TimeSpan.FromSeconds(30) });
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + host.Port + "/ws"), CancellationToken.None);
        await ReceiveAsync(ws);

        // A word that opens with "[" in the middle of the line stays one word.
        await SendAsync(ws, "{\"cmd\":\"DEVICE ADD\",\"args\":[\"[L]\",\"pjlink\",\"10.0.0.20\"],\"id\":\"w1\"}");
        await ReceiveUntilAsync(ws, m => m.Contains("\"id\":\"w1\"", StringComparison.Ordinal));
        // JSON as the last argument rides bare, so the verb reads it from the line's raw text.
        await SendAsync(ws, "{\"cmd\":\"SET\",\"args\":[\"sf_a\",\"content\",{\"mode\":\"uv\"}],\"id\":\"w2\"}");
        await ReceiveUntilAsync(ws, m => m.Contains("\"id\":\"w2\"", StringComparison.Ordinal));

        var lines = endpoint.Lines.Select(l => l.Line).ToList();
        Assert.Equal(["DEVICE", "ADD", "[L]", "pjlink", "10.0.0.20"], lines[0].Words);
        Assert.Equal(["SET", "sf_a", "content", "{\"mode\":\"uv\"}"], lines[1].Words);
        Assert.Equal("{\"mode\":\"uv\"}", lines[1].RestAfter(3));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task AFrameMayOvertakeTheReceiptOfTheCommandThatMovedIt()
    {
        // The node publishes its change before it answers the line, so the sender may put the frame on the socket
        // first; holding the answer makes that order certain instead of a matter of thread scheduling.
        var endpoint = new FakeEndpoint { HoldReply = TimeSpan.FromMilliseconds(300) };
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true), new HttpHostOptions { BeatEvery = TimeSpan.FromSeconds(30) });
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + host.Port + "/ws"), CancellationToken.None);
        Assert.Contains("\"seq\":1,", await ReceiveAsync(ws), StringComparison.Ordinal);

        await SendAsync(ws, "{\"cmd\":\"BLACKOUT ON\",\"id\":\"b7\"}");
        var frame = await ReceiveAsync(ws);
        Assert.Contains("\"seq\":2,", frame, StringComparison.Ordinal);
        Assert.Contains("\"blackout\":true", frame, StringComparison.Ordinal);
        Assert.StartsWith("{\"type\":\"receipt\",\"id\":\"b7\",\"ok\":true,\"stage\":\"applied\"", await ReceiveAsync(ws), StringComparison.Ordinal);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task PlainAndTlsShareOnePortAndTheCertificateIsTheNodesOwn()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-tls-");
        try
        {
            using var identity = TlsIdentity.LoadOrCreate(folder.FullName, "Stage left", DateTimeOffset.UtcNow);
            await using var host = await StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: true), new HttpHostOptions { Tls = identity });
            string? seen = null;
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                {
                    seen = cert is null ? null : TlsFingerprint.Of(cert);
                    return seen == identity.Fingerprint;
                },
            };
            using var https = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1:" + host.Port) };
            Assert.StartsWith("{\"v\":1", await https.GetStringAsync("/api/state"), StringComparison.Ordinal);
            Assert.Equal(identity.Fingerprint, seen);

            using var plain = Client(host);
            Assert.StartsWith("{\"v\":1", await plain.GetStringAsync("/api/state"), StringComparison.Ordinal);
            Assert.Equal(identity.PublicDer, await plain.GetByteArrayAsync("/tls.cer"));
            Assert.Contains(identity.Fingerprint["sha256:".Length..], await plain.GetStringAsync("/tls"), StringComparison.Ordinal);
            using var der = X509CertificateLoader.LoadCertificate(identity.PublicDer);
            Assert.Equal(identity.Fingerprint, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(der.RawData)));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MediaIsServedByHashWithRangesAndListedInTheManifest()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("playmap-media-").FullName, "clip.bin");
        var bytes = RandomNumberGenerator.GetBytes(100_000);
        await File.WriteAllBytesAsync(file, bytes);
        var store = new FakeMediaStore();
        var hash = store.Add(file);
        try
        {
            await using var host = await StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: true), new HttpHostOptions { Media = store });
            using var c = Client(host);
            using var manifest = JsonDocument.Parse(await c.GetStringAsync("/api/media"));
            var entry = Assert.Single(manifest.RootElement.EnumerateArray());
            Assert.Equal(hash, entry.GetProperty("hash").GetString());
            Assert.Equal(100_000, entry.GetProperty("size").GetInt64());

            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/media/" + hash);
            request.Headers.Range = new RangeHeaderValue(1000, 1999);
            using var response = await c.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(bytes[1000..2000], await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/media/" + new string('0', 64))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/media/..%2F..%2Fetc%2Fpasswd")).StatusCode);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    internal static async Task<string> ReceiveAsync(ClientWebSocket ws, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        var count = 0;
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), cts.Token);
            count += r.Count;
        }
        while (!r.EndOfMessage);
        return Encoding.UTF8.GetString(buffer, 0, count);
    }

    internal static async Task<string> ReceiveUntilAsync(ClientWebSocket ws, Func<string, bool> match, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var m = await ReceiveAsync(ws, Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds));
            if (match(m))
                return m;
        }
    }

    /// <summary>A command's receipt and the first state frame at or past <paramref name="seq"/>, in whichever order they
    /// come: a receipt answers the command, a frame is the node's state latest-wins, and the node publishes a change
    /// before it answers the line that made it — so the frame may be on the socket first. Nothing read is dropped
    /// before both are in.</summary>
    internal static async Task<(string Receipt, string Frame)> ReceiptAndFrameAsync(ClientWebSocket ws, string id, long seq, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? receipt = null;
        string? frame = null;
        while (receipt is null || frame is null)
        {
            var m = await ReceiveAsync(ws, Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds));
            using var doc = JsonDocument.Parse(m);
            var r = doc.RootElement;
            var type = r.GetProperty("type").GetString();
            if (type == "receipt" && r.GetProperty("id").GetString() == id)
                receipt ??= m;
            else if (type == "state" && r.GetProperty("seq").GetInt64() >= seq)
                frame ??= m;
        }
        return (receipt, frame);
    }

    internal static Task SendAsync(ClientWebSocket ws, string json) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
}

/// <summary>A library of files by hash, for the media routes and transfers.</summary>
internal sealed class FakeMediaStore : IMediaStore
{
    private readonly Dictionary<string, MediaFile> _files = new(StringComparer.Ordinal);

    public string Add(string path, string? claimedHash = null)
    {
        var hash = claimedHash ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        _files[hash] = new MediaFile(hash, path, new FileInfo(path).Length, Path.GetFileName(path));
        return hash;
    }

    public MediaFile? Lookup(string sha256) => _files.TryGetValue(sha256, out var f) ? f : null;

    public IReadOnlyList<MediaFile> Manifest() => _files.Values.ToList();
}
