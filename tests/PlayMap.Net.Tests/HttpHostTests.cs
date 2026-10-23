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
        // The poll's limit is counted on a clock only the test moves, so nothing but a change or the test's own move of that clock
        // ends a poll, however late the test's next step runs.
        var endpoint = new FakeEndpoint();
        var time = new ManualTime();
        var limit = TimeSpan.FromMilliseconds(600);
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true), new HttpHostOptions { LongPollMax = limit, Time = time });
        using var c = Client(host);

        // Asked about an older STATE, it answers the one it holds at once, with no wait.
        Assert.Contains("\"seq\":1,", await c.GetStringAsync("/api/state?since=0"), StringComparison.Ordinal);
        Assert.Equal(0, time.Made);

        // Asked about the one it holds, it waits: a wake that brings nothing newer is no answer, and the next change is.
        var waiting = c.GetStringAsync("/api/state?since=1");
        await WaitsOnItsLimitAsync(time, 1, waiting);
        endpoint.Touch();
        await WaitsOnItsLimitAsync(time, 2, waiting);
        endpoint.Publish();
        Assert.Contains("\"seq\":2,", await waiting.WaitAsync(TimeSpan.FromSeconds(10)), StringComparison.Ordinal);

        // With nothing newer it answers the one it holds once its limit has passed since it took the request, and not a tick
        // before: a wake on the way waits only what is left.
        var quiet = c.GetStringAsync("/api/state?since=2");
        await WaitsOnItsLimitAsync(time, 3, quiet);
        time.Advance(limit / 2);
        endpoint.Touch();
        await WaitsOnItsLimitAsync(time, 4, quiet);
        time.Advance(limit / 2 - TimeSpan.FromTicks(1));
        Assert.Equal((TaskStatus.WaitingForActivation, 1), (quiet.Status, time.Waiting));
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Contains("\"seq\":2,", await quiet.WaitAsync(TimeSpan.FromSeconds(10)), StringComparison.Ordinal);
        Assert.Equal((4, 0), (time.Made, time.Waiting));
    }

    [Fact]
    public async Task ALongPollWithUnderAMillisecondOfItsLimitLeftWaitsThatOutOnTheClock()
    {
        // A wake that brings nothing newer with a part of a millisecond of the limit left waits for that part on a timer, as for any
        // rest of the limit, and the poll answers once the limit has passed.
        var endpoint = new FakeEndpoint();
        var time = new ManualTime();
        var limit = TimeSpan.FromMilliseconds(600);
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true), new HttpHostOptions { LongPollMax = limit, Time = time });
        using var c = Client(host);
        var poll = c.GetStringAsync("/api/state?since=1");
        await WaitsOnItsLimitAsync(time, 1, poll);
        time.Advance(limit - TimeSpan.FromTicks(5_000));
        endpoint.Touch();
        await WaitsOnItsLimitAsync(time, 2, poll);
        time.Advance(TimeSpan.FromTicks(4_999));
        Assert.Equal((TaskStatus.WaitingForActivation, 1), (poll.Status, time.Waiting));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Contains("\"seq\":1,", await poll.WaitAsync(TimeSpan.FromSeconds(10)), StringComparison.Ordinal);
        Assert.Equal((2, 0), (time.Made, time.Waiting));
    }

    [Fact]
    public async Task AWaitForAChangeEndsNoSoonerThanItsTimeThoughThatIsNotWholeMilliseconds()
    {
        // The runtime's timed wait counts whole milliseconds, dropping a part of one, and ends at once on less than one; the signal's
        // wait is never shorter than it was asked for.
        var time = new ManualTime();
        var signal = new ChangeSignal();
        foreach (var max in new[] { TimeSpan.FromTicks(5_000), TimeSpan.FromTicks(15_000) })
        {
            var wait = signal.WaitAsync(signal.Next, max, time, CancellationToken.None);
            time.Advance(max - TimeSpan.FromTicks(1));
            Assert.Equal((TaskStatus.WaitingForActivation, 1), (wait.Status, time.Waiting));
            time.Advance(TimeSpan.FromMilliseconds(1));
            Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, time.Waiting);
        }

        // A change within the wait ends it with true, the clock unmoved.
        var changed = signal.WaitAsync(signal.Next, TimeSpan.FromTicks(5_000), time, CancellationToken.None);
        Assert.Equal((TaskStatus.WaitingForActivation, 1), (changed.Status, time.Waiting));
        signal.Fire();
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(3, time.Made);
    }

    [Fact]
    public async Task OnTheSystemClockALongPollWithNothingNewerAnswersNoSoonerThanItsLimit()
    {
        // The clock a node's web port counts on unless it is given another. The count starts before the request is sent, so the poll
        // answers after it has counted its limit however the threads run, and the test reads no other bound of real time.
        Assert.Same(TimeProvider.System, new HttpHostOptions().Time);
        var endpoint = new FakeEndpoint();
        var limit = TimeSpan.FromMilliseconds(50);
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true), new HttpHostOptions { LongPollMax = limit });
        using var c = Client(host);
        var asked = System.Diagnostics.Stopwatch.GetTimestamp();
        var answer = await c.GetStringAsync("/api/state?since=1").WaitAsync(TimeSpan.FromSeconds(20));
        var took = System.Diagnostics.Stopwatch.GetElapsedTime(asked);
        Assert.Contains("\"seq\":1,", answer, StringComparison.Ordinal);
        Assert.True(took >= limit, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"answered {took.TotalMilliseconds:F3} ms after it was asked, inside its {limit.TotalMilliseconds} ms limit"));
    }

    [Fact]
    public async Task ALongPollTheWebPortsStopCutsIsAnsweredThatItStopsNotWithAnEmptyState()
    {
        // A poll waiting when the node's web port stops has seen no newer STATE and has not reached its limit: it is refused with the
        // reason, as a request the port cannot serve, never a 200 without a STATE in it.
        var endpoint = new FakeEndpoint();
        var time = new ManualTime();
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true), new HttpHostOptions { LongPollMax = TimeSpan.FromSeconds(25), Time = time });
        using var c = Client(host);
        var poll = c.GetAsync("/api/state?since=1");
        await WaitsOnItsLimitAsync(time, 1, poll);
        await host.DisposeAsync();
        using var answer = await poll.WaitAsync(TimeSpan.FromSeconds(10));
        var body = await answer.Content.ReadAsStringAsync();
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "application/json; charset=utf-8"), (answer.StatusCode, answer.Content.Headers.ContentType?.ToString()));
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(HttpHost.StoppingWords, json.RootElement.GetProperty("error").GetString());
        Assert.Equal((1, 0), (time.Made, time.Waiting));
    }

    /// <summary>Waits until the poll has made its <paramref name="made"/>th wait on the clock and that wait alone stands, then holds that
    /// the poll has not answered: from here only a change or a move of the clock ends it.</summary>
    private static async Task WaitsOnItsLimitAsync(ManualTime time, int made, Task poll)
    {
        Assert.True(await Eventually.TrueAsync(() => (time.Made == made && time.Waiting == 1) || poll.IsCompleted, 20000),
            $"the poll never waited on its limit: {time.Made} waits made, {time.Waiting} standing");
        Assert.Equal(TaskStatus.WaitingForActivation, poll.Status);
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
        // first; the answer is held until the test has the frame, so that order is certain however the threads run.
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoint = new FakeEndpoint { HoldReplyUntil = answer.Task };
        await using var host = await StartAsync(endpoint, TestSettings.Make(trustLoopback: true), new HttpHostOptions { BeatEvery = TimeSpan.FromSeconds(30) });
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + host.Port + "/ws"), CancellationToken.None);
        Assert.Contains("\"seq\":1,", await ReceiveAsync(ws), StringComparison.Ordinal);

        await SendAsync(ws, "{\"cmd\":\"BLACKOUT ON\",\"id\":\"b7\"}");
        var frame = await ReceiveAsync(ws);
        Assert.Contains("\"seq\":2,", frame, StringComparison.Ordinal);
        Assert.Contains("\"blackout\":true", frame, StringComparison.Ordinal);
        answer.SetResult();
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

    [Fact]
    public async Task AMediaFileAnswersOneRangeItsHashAsItsTagAndTheWholeFileForAnythingElse()
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
            async Task<(HttpStatusCode Status, string? Range, byte[] Body, HttpResponseMessage Answer)> AskAsync(string range, string? ifRange = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/media/" + hash);
                request.Headers.TryAddWithoutValidation("Range", range);
                if (ifRange is not null)
                    request.Headers.TryAddWithoutValidation("If-Range", ifRange);
                var response = await c.SendAsync(request);
                return (response.StatusCode, response.Content.Headers.ContentRange?.ToString(), await response.Content.ReadAsByteArrayAsync(), response);
            }

            var (status, range, body, answer) = await AskAsync("bytes=99900-");
            using (answer)
            {
                Assert.Equal((HttpStatusCode.PartialContent, "bytes 99900-99999/100000"), (status, range));
                Assert.Equal(bytes[99_900..], body);
                Assert.Equal("\"" + hash + "\"", answer.Headers.ETag?.ToString());
                Assert.Equal("bytes", Assert.Single(answer.Headers.AcceptRanges));
            }
            (status, range, body, answer) = await AskAsync("bytes=-100");
            using (answer)
            {
                Assert.Equal((HttpStatusCode.PartialContent, "bytes 99900-99999/100000"), (status, range));
                Assert.Equal(bytes[99_900..], body);
            }
            (status, range, body, answer) = await AskAsync("bytes=0-9", "\"" + hash + "\"");
            using (answer)
            {
                Assert.Equal((HttpStatusCode.PartialContent, "bytes 0-9/100000"), (status, range));
                Assert.Equal(bytes[..10], body);
            }
            // Past the end: none of it, and the file's length.
            (status, range, body, answer) = await AskAsync("bytes=100000-");
            using (answer)
                Assert.Equal((HttpStatusCode.RequestedRangeNotSatisfiable, "bytes */100000", 0), (status, range, body.Length));
            // Several ranges, or a range for another copy than this one: the whole file.
            foreach (var (several, condition) in new[] { ("bytes=0-9,20-29", (string?)null), ("bytes=0-9", "\"another\"") })
            {
                (status, range, body, answer) = await AskAsync(several, condition);
                using (answer)
                {
                    Assert.Equal((HttpStatusCode.OK, (string?)null), (status, range));
                    Assert.Equal(bytes, body);
                }
            }
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
