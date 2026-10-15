using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Phone;
using PlayMap.Net.Phone;

namespace PlayMap.Net.Tests.Phone;

/// <summary>Pairing a phone: a ticket in the link's fragment that works once and expires, a key of its own kept only as a digest,
/// the page served with its strict headers, the socket admitted only by ticket or key and only from the node's own page, wrong
/// tickets locked out, the protocol's limits closing the socket with the right code.</summary>
public sealed class PhoneInviteTests
{
    [Fact]
    public async Task ATicketWorksOnceAndTheKeyItGivesIsKeptOnlyAsADigest()
    {
        await using var node = await PhoneNode.StartAsync();
        var invite = node.Invite();
        var ticket = PhoneNode.TicketOf(invite);
        Assert.Matches("^[A-Z2-7]{32}$", ticket);
        Assert.Equal(ticket, invite.Ticket);
        var url = new Uri(invite.Url);
        // The ticket rides only in the fragment, which a browser never sends.
        Assert.Equal("/scan/", url.AbsolutePath);
        Assert.Equal("", url.Query);
        Assert.DoesNotContain(ticket, url.GetLeftPart(UriPartial.Query), StringComparison.Ordinal);
        Assert.Equal(QrMatrix.Encode(invite.Url).ToBase64Bitset(), invite.Qr.ToBase64Bitset());

        await using var phone = await PhoneClient.ConnectAsync(node.Port);
        await phone.SendAsync(PhoneClient.Hello(ticket: ticket));
        var welcome = await phone.ReceiveTypeAsync("welcome");
        var id = welcome.GetProperty("phone").GetProperty("id").GetString()!;
        var key = welcome.GetProperty("key").GetString()!;
        Assert.Matches("^ph[0-9a-f]{8}$", id);
        Assert.Equal("Pixel 8", welcome.GetProperty("phone").GetProperty("name").GetString());
        Assert.Equal(6, welcome.GetProperty("limits").GetProperty("maxInFlight").GetInt32());
        Assert.Equal(16 << 20, welcome.GetProperty("limits").GetProperty("maxBinary").GetInt32());
        Assert.True(await Eventually.TrueAsync(() => node.Hub.Phones().Any(p => p.Id == id && p.Connected)));

        var file = await File.ReadAllTextAsync(node.KeysPath);
        Assert.DoesNotContain(key, file, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket, file, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse(file))
            Assert.Equal(PhoneKeys.Digest(key), doc.RootElement.GetProperty("phones")[0].GetProperty("keySha256").GetString());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(node.KeysPath));

        // Used once: the same ticket again is refused.
        await using var again = await PhoneClient.ConnectAsync(node.Port);
        await again.SendAsync(PhoneClient.Hello(ticket: ticket));
        var (status, words) = await again.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        Assert.Contains("not known or was used", words, StringComparison.Ordinal);

        // The key the phone kept brings it back as itself, with no new key.
        await phone.DisposeAsync();
        await using var back = await PhoneClient.ConnectAsync(node.Port);
        await back.SendAsync(PhoneClient.Hello(key: key));
        var rewelcome = await back.ReceiveTypeAsync("welcome");
        Assert.Equal(id, rewelcome.GetProperty("phone").GetProperty("id").GetString());
        Assert.False(rewelcome.TryGetProperty("key", out _));
        Assert.Single(node.Hub.Phones());
    }

    [Fact]
    public async Task ATicketExpires()
    {
        await using var node = await PhoneNode.StartAsync();
        var ticket = PhoneNode.TicketOf(node.Invite());
        node.Clock.Now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);
        await using var phone = await PhoneClient.ConnectAsync(node.Port);
        await phone.SendAsync(PhoneClient.Hello(ticket: ticket));
        var (status, words) = await phone.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        Assert.StartsWith("ERR ticket expired", words, StringComparison.Ordinal);
        Assert.Empty(node.Hub.Phones());
    }

    [Fact]
    public async Task TheSocketWithoutATicketOrKeyIsRefused()
    {
        await using var node = await PhoneNode.StartAsync();
        await using var bare = await PhoneClient.ConnectAsync(node.Port);
        await bare.SendAsync(new { t = "hello", v = 1, camera = new { label = "x", width = 320, height = 240 } });
        var (status, words) = await bare.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        Assert.Equal("a hello without its fields", words);

        await using var early = await PhoneClient.ConnectAsync(node.Port);
        await early.SendAsync(new { t = "ping", t1 = 1.0 });
        Assert.Equal("hello first", (await early.ClosedAsync()).Words);

        await using var key = await PhoneClient.ConnectAsync(node.Port);
        await key.SendAsync(PhoneClient.Hello(key: "not-a-key"));
        Assert.Contains("key is not known", (await key.ClosedAsync()).Words, StringComparison.Ordinal);

        // Without the subprotocol the upgrade is not taken at all.
        await Assert.ThrowsAnyAsync<WebSocketException>(() => PhoneClient.ConnectAsync(node.Port, subprotocol: false));
        Assert.Empty(node.Hub.Phones());
    }

    [Fact]
    public async Task APageOfAnotherOriginMayNotOpenTheSocket()
    {
        await using var node = await PhoneNode.StartAsync();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => PhoneClient.ConnectAsync(node.Port, origin: "https://evil.example"));
        // The node's own page may.
        await using var own = await PhoneClient.ConnectAsync(node.Port, origin: $"http://127.0.0.1:{node.Port}");
        await own.SendAsync(PhoneClient.Hello(ticket: PhoneNode.TicketOf(node.Invite())));
        await own.ReceiveTypeAsync("welcome");
    }

    /// <summary>The upgrade as a browser sends it, with the Host it names.</summary>
    private static async Task<HttpResponseMessage> UpgradeAsync(PhoneNode node, string host)
    {
        using var http = new HttpClient { BaseAddress = node.Base, Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/scan/ws");
        request.Headers.Host = host;
        request.Headers.Connection.Add("Upgrade");
        request.Headers.Upgrade.ParseAdd("websocket");
        request.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(new byte[16]));
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Protocol", PhoneProtocol.SubProtocol);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    [Fact]
    public async Task TheSocketAnswersOnlyByTheNodesOwnAddressOrNames()
    {
        await using var node = await PhoneNode.StartAsync();
        // A page that pointed a name of its own at this machine (DNS rebinding) is turned away before any ticket is read.
        using (var foreign = await UpgradeAsync(node, $"evil.example:{node.Port}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            using var body = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync());
            Assert.Contains("node's address or its own names", body.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
        }
        // The address the invite's link gives is the node's own.
        using (var own = await UpgradeAsync(node, $"127.0.0.1:{node.Port}"))
            Assert.Equal(HttpStatusCode.SwitchingProtocols, own.StatusCode);
    }

    [Fact]
    public async Task FiveWrongTicketsInAMinuteLockTheAddressOut()
    {
        await using var node = await PhoneNode.StartAsync();
        for (var i = 0; i < 5; i++)
        {
            await using var wrong = await PhoneClient.ConnectAsync(node.Port);
            await wrong.SendAsync(PhoneClient.Hello(ticket: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
            Assert.Contains("not known", (await wrong.ClosedAsync()).Words, StringComparison.Ordinal);
        }
        Assert.True(node.Hub.IsLockedOut("127.0.0.1"));
        // Even a good ticket is not tried from a locked address.
        var good = PhoneNode.TicketOf(node.Invite());
        await Assert.ThrowsAnyAsync<WebSocketException>(() => PhoneClient.ConnectAsync(node.Port));
        node.Clock.Now += TimeSpan.FromSeconds(61);
        Assert.False(node.Hub.IsLockedOut("127.0.0.1"));
        await using var phone = await PhoneClient.ConnectAsync(node.Port);
        await phone.SendAsync(PhoneClient.Hello(ticket: good));
        await phone.ReceiveTypeAsync("welcome");
    }

    private static async Task<PhoneClient> JoinedAsync(PhoneNode node)
    {
        var phone = await PhoneClient.ConnectAsync(node.Port);
        await phone.SendAsync(PhoneClient.Hello(ticket: PhoneNode.TicketOf(node.Invite())));
        await phone.ReceiveTypeAsync("welcome");
        return phone;
    }

    [Fact]
    public async Task AnUnknownMessageClosesTheSocketWith1008()
    {
        await using var node = await PhoneNode.StartAsync();
        await using var phone = await JoinedAsync(node);
        await phone.SendAsync(new { t = "nope" });
        var (status, words) = await phone.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        Assert.Equal("unknown message", words);
        Assert.True(await Eventually.TrueAsync(() => node.Hub.Phones().All(p => !p.Connected)));
        Assert.Contains("unknown message", node.Hub.Phones()[0].Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextOverItsLimitClosesWith1009()
    {
        await using var node = await PhoneNode.StartAsync();
        await using var phone = await JoinedAsync(node);
        await phone.SendTextAsync("{\"t\":\"state\",\"pad\":\"" + new string('x', 64 * 1024) + "\"}");
        var (status, words) = await phone.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, status);
        Assert.Equal("message too big", words);
    }

    [Fact]
    public async Task BinaryOverItsLimitClosesWith1009AndABrokenFrameWith1007()
    {
        await using var node = await PhoneNode.StartAsync(o => o with { MaxBinaryBytes = 256 * 1024 });
        await using (var phone = await JoinedAsync(node))
        {
            await phone.SendBinaryAsync(new byte[256 * 1024 + 1]);
            var (status, words) = await phone.ClosedAsync();
            Assert.Equal(WebSocketCloseStatus.MessageTooBig, status);
            Assert.Equal("message too big", words);
        }
        await using (var phone = await JoinedAsync(node))
        {
            var header = new Pmf1Header("pattern", 1, 1, 320, 240, new Core.Scan.PixelRect(0, 0, 32, 32), Pmf1.Gray8, Pmf1.None, 1, 0, 0);
            var frame = Pmf1.Write(header, new byte[32 * 32]);
            frame[^1] ^= 0xFF;
            await phone.SendBinaryAsync(frame);
            var (status, words) = await phone.ClosedAsync();
            Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, status);
            Assert.Contains("CRC-32", words, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task APictureIsKeptOnlyAtTheCamerasSizeAndOnlyWhenACueStillWaitsForIt()
    {
        await using var node = await PhoneNode.StartAsync();
        await using (var phone = await JoinedAsync(node))
        {
            // A tiny frame that says it is a huge picture: refused before anything is allocated for it.
            var header = new Pmf1Header("pattern", 1, 1, 16384, 16384, new Core.Scan.PixelRect(0, 0, 1, 1), Pmf1.Gray8, Pmf1.None, 1, 0, 0);
            await phone.SendBinaryAsync(Pmf1.Write(header, new byte[1]));
            var (status, words) = await phone.ClosedAsync();
            Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, status);
            Assert.Equal("bad frame: a picture whose size is not the camera's", words);
        }
        await using (var phone = await JoinedAsync(node))
        {
            // Pictures no cue waits for (cancelled, timed out, never asked): each acknowledged and dropped, however many come.
            for (var seq = 1; seq <= 30; seq++)
            {
                await phone.SendAsync(new { t = "seen", cue = 1000 + seq, seq, tag = 0, capture = 0 });
                var frame = Pmf1.Write(new Pmf1Header("pattern", seq, 1000 + seq, 320, 240, new Core.Scan.PixelRect(0, 0, 320, 240), Pmf1.Gray8, Pmf1.Deflate,
                    1, 0, 0), new byte[320 * 240]);
                await phone.SendBinaryAsync(frame);
                Assert.Equal(seq, (await phone.ReceiveTypeAsync("ack")).GetProperty("seq").GetInt64());
            }
            Assert.Equal(WebSocketState.Open, phone.Socket.State);
            Assert.Single(node.Hub.Phones(), p => p.Connected);
        }
    }

    [Fact]
    public async Task APhoneThatSendsNothingIsEndedWithWords()
    {
        await using var node = await PhoneNode.StartAsync(o => o with { QuietTimeout = TimeSpan.FromMilliseconds(500) });
        await using var phone = await JoinedAsync(node);
        var (status, words) = await phone.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        Assert.Equal("stopped sending: nothing for 0.5 s", words);
        Assert.True(await Eventually.TrueAsync(() => !node.Hub.Phones()[0].Connected));
        Assert.Equal("Pixel 8 stopped sending: nothing for 0.5 s", node.Hub.Phones()[0].Words);
    }

    [Fact]
    public async Task APhoneForgottenWhileItsJoinIsInFlightIsRefusedAndItsKeyStaysForgotten()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = false;
        await using var node = await PhoneNode.StartAsync();
        node.Hub.Join = async (j, ct) =>
        {
            if (Volatile.Read(ref gated))
            {
                joining.TrySetResult();
                await hold.Task.WaitAsync(ct);
            }
            return "cm00000001";
        };
        string key;
        await using (var first = await PhoneClient.ConnectAsync(node.Port))
        {
            await first.SendAsync(PhoneClient.Hello(ticket: PhoneNode.TicketOf(node.Invite())));
            key = (await first.ReceiveTypeAsync("welcome")).GetProperty("key").GetString()!;
        }
        var id = node.Hub.Phones()[0].Id;
        Assert.True(await Eventually.TrueAsync(() => !node.Hub.Phones()[0].Connected));
        Volatile.Write(ref gated, true);
        await using var again = await PhoneClient.ConnectAsync(node.Port);
        await again.SendAsync(PhoneClient.Hello(key: key));
        await joining.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // PHONE FORGET while the join waits on the node: the join is refused, and the forgotten key is not written back.
        Assert.Equal([id], node.Hub.Forget(id));
        hold.TrySetResult();
        var (status, words) = await again.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        Assert.Equal("was forgotten: its key no longer opens anything", words);
        Assert.Empty(node.Hub.Phones());
        Assert.DoesNotContain(id, await File.ReadAllTextAsync(node.KeysPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJoinTheNodeCannotFinishLeavesNoPhoneAndItsTicketHoldsForAnotherTry()
    {
        var refuse = true;
        await using var node = await PhoneNode.StartAsync();
        node.Hub.Join = (j, ct) => Volatile.Read(ref refuse)
            ? throw new InvalidOperationException("mapping is locked while the show is armed — SHOW DISARM first")
            : Task.FromResult("cm00000001");
        var ticket = PhoneNode.TicketOf(node.Invite());
        await using (var first = await PhoneClient.ConnectAsync(node.Port))
        {
            await first.SendAsync(PhoneClient.Hello(ticket: ticket));
            var (status, words) = await first.ClosedAsync();
            Assert.Equal(WebSocketCloseStatus.InternalServerError, status);
            Assert.Equal("the node could not add this phone's camera: mapping is locked while the show is armed — SHOW DISARM first", words);
        }
        Assert.Empty(node.Hub.Phones());
        Volatile.Write(ref refuse, false);
        await using var second = await PhoneClient.ConnectAsync(node.Port);
        await second.SendAsync(PhoneClient.Hello(ticket: ticket));
        var welcome = await second.ReceiveTypeAsync("welcome");
        Assert.Equal("cm00000001", welcome.GetProperty("camera").GetString());
        Assert.True(Assert.Single(node.Hub.Phones()).Connected);
    }

    [Fact]
    public async Task AWelcomeThatNeverGoesLeavesNoPhantomPhone()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var node = await PhoneNode.StartAsync();
        node.Hub.Join = async (j, ct) =>
        {
            joining.TrySetResult();
            await hold.Task;
            return "cm00000001";
        };
        var phone = await PhoneClient.ConnectAsync(node.Port);
        await phone.SendAsync(PhoneClient.Hello(ticket: PhoneNode.TicketOf(node.Invite())));
        await joining.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The node stops while the join is in flight: the welcome never goes, so no phone is left connected or paired.
        await node.Hub.DisposeAsync();
        hold.TrySetResult();
        var (status, _) = await phone.ClosedAsync();
        Assert.NotEqual(WebSocketCloseStatus.NormalClosure, status);
        Assert.Empty(node.Hub.Phones());
        await phone.DisposeAsync();
    }

    [Fact]
    public async Task OnlyAPhoneConnectedNowIsExemptFromTheConnectedLimit()
    {
        await using var node = await PhoneNode.StartAsync(o => o with { MaxConnected = 1 });
        string keyA;
        await using (var a = await PhoneClient.ConnectAsync(node.Port))
        {
            await a.SendAsync(PhoneClient.Hello(ticket: PhoneNode.TicketOf(node.Invite("A"))));
            keyA = (await a.ReceiveTypeAsync("welcome")).GetProperty("key").GetString()!;
        }
        Assert.True(await Eventually.TrueAsync(() => node.Hub.Connected == 0));
        await using var b = await PhoneClient.ConnectAsync(node.Port);
        await b.SendAsync(PhoneClient.Hello(ticket: PhoneNode.TicketOf(node.Invite("B"))));
        await b.ReceiveTypeAsync("welcome");
        // A joined before but is not connected now: the limit holds for it as for any phone.
        await using var again = await PhoneClient.ConnectAsync(node.Port);
        await again.SendAsync(PhoneClient.Hello(key: keyA));
        Assert.Equal("1 phones are connected already", (await again.ClosedAsync()).Words);
        Assert.Equal(1, node.Hub.Connected);
    }

    [Fact]
    public void ACloseReasonFitsItsBytesOnACharacterBoundary()
    {
        Assert.Equal("short words", PhoneSession.CloseReason("short words"));
        var reason = PhoneSession.CloseReason(new string('é', 100) + "😀😀");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(reason) <= 123, reason);
        Assert.EndsWith("…", reason, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(reason[^2]));
    }

    [Fact]
    public async Task ThePageCarriesItsStrictHeadersAndNothingElseUnderScanIsServed()
    {
        await using var node = await PhoneNode.StartAsync();
        using var http = new HttpClient { BaseAddress = node.Base, Timeout = TimeSpan.FromSeconds(30) };
        using (var page = await http.GetAsync("/scan/"))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var csp = page.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
            Assert.Contains("script-src 'self';", csp, StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
            Assert.DoesNotContain("unsafe", csp, StringComparison.Ordinal);
            Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("camera=(self)", page.Headers.GetValues("Permissions-Policy").Single(), StringComparison.Ordinal);
            Assert.True(page.Headers.CacheControl!.NoStore);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("<script type=\"module\" src=\"app.js\"></script>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("style=", html, StringComparison.Ordinal);
        }
        using (var script = await http.GetAsync("/scan/app.js"))
        {
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
            Assert.NotNull(script.Headers.ETag);
            Assert.Contains("default-src 'self'", script.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        }
        foreach (var path in new[] { "/scan/nothing.js", "/scan/../api/state", "/scan/phones.json" })
        {
            using var r = await http.GetAsync(path);
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        }
        using (var post = await http.PostAsync("/scan/", new StringContent("x")))
            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        // The module opens nothing of the node's own: its routes still want the token.
        using (var state = await http.GetAsync("/api/state"))
            Assert.Equal(HttpStatusCode.Unauthorized, state.StatusCode);
        Assert.Contains("index.html", PhoneWebModule.FileNames);
        Assert.Contains("tags.js", PhoneWebModule.FileNames);
    }

    [Fact]
    public async Task PhonesSwitchedOffAreRefusedWithWords()
    {
        await using var node = await PhoneNode.StartAsync(o => o with { Enabled = () => false });
        using var http = new HttpClient { BaseAddress = node.Base, Timeout = TimeSpan.FromSeconds(30) };
        using var page = await http.GetAsync("/scan/");
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Contains("switched off", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtMostTheConnectedLimitJoinAndAForgottenPhoneIsCutOff()
    {
        await using var node = await PhoneNode.StartAsync(o => o with { MaxConnected = 1 });
        await using var first = await JoinedAsync(node);
        await using var second = await PhoneClient.ConnectAsync(node.Port);
        await second.SendAsync(PhoneClient.Hello(ticket: PhoneNode.TicketOf(node.Invite())));
        Assert.Contains("1 phones are connected already", (await second.ClosedAsync()).Words, StringComparison.Ordinal);

        var id = node.Hub.Phones().Single(p => p.Connected).Id;
        Assert.Equal([id], node.Hub.Forget(id));
        var (status, words) = await first.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        Assert.Contains("forgotten", words, StringComparison.Ordinal);
        Assert.Empty(node.Hub.Forget(id));
        Assert.DoesNotContain(id, await File.ReadAllTextAsync(node.KeysPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClockExchangeTurnsPageTimeIntoNodeTime()
    {
        long nodeNs = 5_000_000_000;
        await using var node = await PhoneNode.StartAsync(o => o with { MonoNs = () => Interlocked.Read(ref nodeNs) });
        await using var phone = await JoinedAsync(node);
        // The page's clock reads 1000 ms when the node's reads 5000 ms: the node is 4000 ms ahead; the link is symmetric.
        await phone.SendAsync(new { t = "ping", t1 = 1000.0 });
        var pong = await phone.ReceiveTypeAsync("pong");
        Assert.Equal(5000.0, pong.GetProperty("t2").GetDouble());
        await phone.SendAsync(new { t = "ping", t1 = 2000.0, prev = new { t1 = 998.0, t2 = 5000.0, t3 = 5000.0, t4 = 1002.0 } });
        var clock = await phone.ReceiveTypeAsync("clock");
        Assert.Equal(4000.0, clock.GetProperty("offsetMs").GetDouble());
        Assert.Equal(2.0, clock.GetProperty("uncertaintyMs").GetDouble());
        var session = node.Hub.Session(node.Hub.Phones()[0].Id)!;
        Assert.Equal(6_500_000_000, session.ToNodeNs(2500));
    }
}
