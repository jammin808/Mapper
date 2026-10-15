using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Net.Commands;
using PlayMap.Net.Http;
using PlayMap.Net.Wire;

namespace PlayMap.Net.Tests;

/// <summary>The web port on a trusted address (this machine, by default) against a page in the operator's browser: a page from
/// another site reaches none of the node's routes (commands, the JSON socket, MCP), and a page that points its own name at this
/// machine (DNS rebinding) is refused wherever the node would let it in without the token. Controllers that send no Origin, the
/// node's own pages, its own names, names the operator lists, and any request with the token are unchanged.</summary>
public sealed class WebOriginTests
{
    private const string Evil = "http://evil.example";

    private static HttpClient Client(HttpHost host, string? token = null)
    {
        var c = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + host.Port), Timeout = TimeSpan.FromSeconds(30) };
        if (token is not null)
            c.DefaultRequestHeaders.Add("X-PlayMap-Token", token);
        return c;
    }

    private static async Task<(HttpStatusCode Status, string? Error)> SendAsync(HttpClient c, HttpMethod method, string path, string? body = null,
        string? origin = null, string? host = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, path == "/mcp" ? "application/json" : "text/plain");
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        if (host is not null)
            request.Headers.Host = host;
        if (path == "/mcp")
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await c.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        string? error = null;
        if (text.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                error = e.GetString();
        }
        return (response.StatusCode, error);
    }

    [Fact]
    public async Task APageFromAnotherSiteReachesNoneOfTheNodesRoutesOnATrustedAddress()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await HttpHostTests.StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        using var c = Client(host);

        // A command: refused before it reaches the node, so nothing ran.
        Assert.Equal((HttpStatusCode.Forbidden, HttpHost.ForeignOrigin), await SendAsync(c, HttpMethod.Post, "/api/cmd", "BLACKOUT ON", Evil));
        Assert.False(endpoint.Blackout);
        Assert.Empty(endpoint.Lines);
        // STATE, MCP and the JSON socket alike.
        Assert.Equal((HttpStatusCode.Forbidden, HttpHost.ForeignOrigin), await SendAsync(c, HttpMethod.Get, "/api/state", origin: Evil));
        var mcp = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"run","arguments":{"line":"BLACKOUT ON"}}}""";
        Assert.Equal((HttpStatusCode.Forbidden, HttpHost.ForeignOrigin), await SendAsync(c, HttpMethod.Post, "/mcp", mcp, Evil));
        Assert.Empty(endpoint.Lines);
        using (var page = new ClientWebSocket())
        {
            page.Options.SetRequestHeader("Origin", Evil);
            page.Options.CollectHttpResponseDetails = true;
            await Assert.ThrowsAsync<WebSocketException>(() => page.ConnectAsync(new Uri("ws://127.0.0.1:" + host.Port + "/ws"), CancellationToken.None));
            Assert.Equal(HttpStatusCode.Forbidden, page.HttpStatusCode);
        }
        // A sandboxed page says "null": it is not the node's own either.
        Assert.Equal((HttpStatusCode.Forbidden, HttpHost.ForeignOrigin), await SendAsync(c, HttpMethod.Post, "/api/cmd", "BLACKOUT ON", "null"));

        // The node's own page, and a controller that sends no Origin, drive it as before.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(c, HttpMethod.Post, "/api/cmd", "BLACKOUT ON", "http://127.0.0.1:" + host.Port)).Status);
        Assert.True(endpoint.Blackout);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(c, HttpMethod.Post, "/api/cmd", "BLACKOUT OFF")).Status);
        Assert.False(endpoint.Blackout);
        Assert.Equal(2, endpoint.Lines.Count);
    }

    [Fact]
    public async Task ANameAPagePointedAtThisMachineIsRefusedWhereTheNodeAsksNoToken()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await HttpHostTests.StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        using var c = Client(host);
        var rebound = "rebind.evil.example:" + host.Port;
        var words = $"this node answers by its addresses and its own names, not 'rebind.evil.example' — use its address, list the name in the node's web.hosts setting, or send the token";

        // The page's origin is its own name, so it looks same-origin: the host it names is what gives it away.
        Assert.Equal((HttpStatusCode.Forbidden, words),
            await SendAsync(c, HttpMethod.Post, "/api/cmd", "BLACKOUT ON", "http://" + rebound, rebound));
        Assert.Equal((HttpStatusCode.Forbidden, words), await SendAsync(c, HttpMethod.Get, "/api/state", host: rebound));
        Assert.False(endpoint.Blackout);
        Assert.Empty(endpoint.Lines);

        // The line channel would pair it at once on this trusted address: refused before the upgrade.
        Assert.Equal("HTTP/1.1 403 Forbidden", await RawUpgradeStatusAsync(host.Port, rebound, "http://" + rebound));
        Assert.Equal(0, host.LineSockets);
        Assert.Equal("HTTP/1.1 101 Switching Protocols", await RawUpgradeStatusAsync(host.Port, "127.0.0.1:" + host.Port, null));
    }

    [Fact]
    public async Task ItsOwnNamesListedNamesAndTheTokenStillReachIt()
    {
        var endpoint = new FakeEndpoint();
        var settings = TestSettings.Make(trustLoopback: true) with { Web = new WebSettings { Hosts = ["Stage-Left.Venue.LAN."] } };
        await using (var host = await HttpHostTests.StartAsync(endpoint, settings))
        {
            using var c = Client(host);
            foreach (var name in new[] { "localhost", Environment.MachineName, Environment.MachineName + ".local", Dns.GetHostName(), "stage-left.venue.lan", "[::1]" })
                Assert.Equal(HttpStatusCode.OK, (await SendAsync(c, HttpMethod.Get, "/api/state", host: name + ":" + host.Port)).Status);
            // Only localhost itself: a subdomain of it may be resolved by the network's resolver, so a page could hold it.
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(c, HttpMethod.Get, "/api/state", host: "show.localhost:" + host.Port)).Status);
        }

        // Asked for the token (this machine not trusted), a request that carries it may name any host: the token is what lets it in.
        await using var tokenHost = await HttpHostTests.StartAsync(endpoint, TestSettings.Make(trustLoopback: false));
        using var withToken = Client(tokenHost, TestSettings.Token);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(withToken, HttpMethod.Post, "/api/cmd", "BLACKOUT ON", host: "rebind.evil.example:" + tokenHost.Port)).Status);
        Assert.True(endpoint.Blackout);
        using var without = Client(tokenHost);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(without, HttpMethod.Get, "/api/state", host: "rebind.evil.example:" + tokenHost.Port)).Status);
    }

    [Fact]
    public async Task OnATrustedAddressTheTokenStillLetsInARequestThatNamesAnotherHost()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await HttpHostTests.StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        var proxied = "playmap.venue.example:" + host.Port;

        // A proxy on this machine that keeps the Host, or a client with the token using a DNS name: the token is what lets it in.
        using var withToken = Client(host, TestSettings.Token);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(withToken, HttpMethod.Post, "/api/cmd", "BLACKOUT ON", host: proxied)).Status);
        Assert.True(endpoint.Blackout);
        // A wrong one is refused and counted like any wrong token.
        using var wrong = Client(host, "wrong-token-0000");
        Assert.Equal((HttpStatusCode.Unauthorized, WireWords.WrongToken),
            await SendAsync(wrong, HttpMethod.Post, "/api/cmd", "BLACKOUT OFF", host: proxied));
        Assert.True(endpoint.Blackout);
        Assert.Single(endpoint.Lines);

        // The refusals offer the token only where sending it works: not on the line channel, nor on a node that asks none.
        using var anyone = Client(host);
        Assert.Equal((HttpStatusCode.Forbidden, "this node answers by its addresses and its own names, not 'playmap.venue.example' — use its address or list the name in the node's web.hosts setting"),
            await UpgradeAsync(anyone, proxied));
        await using var open = await HttpHostTests.StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: false, token: ""));
        using var c = Client(open);
        Assert.Equal((HttpStatusCode.Forbidden, "this node answers by its addresses and its own names, not 'playmap.venue.example' — use its address or list the name in the node's web.hosts setting"),
            await SendAsync(c, HttpMethod.Get, "/api/state", host: "playmap.venue.example:" + open.Port));
    }

    /// <summary>A WebSocket upgrade for the line channel written by hand, answering the status line.</summary>
    private static async Task<string> RawUpgradeStatusAsync(int port, string hostHeader, string? origin)
    {
        using var tcp = await SendUpgradeAsync(port, hostHeader, origin);
        using var reader = new StreamReader(tcp.GetStream(), Encoding.ASCII);
        return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) ?? "";
    }

    /// <summary>A line-channel upgrade sent through HttpClient (a refusal is an ordinary answer with words), naming a host.</summary>
    private static async Task<(HttpStatusCode Status, string? Error)> UpgradeAsync(HttpClient c, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ws");
        request.Headers.Host = host;
        request.Headers.Connection.Add("Upgrade");
        request.Headers.Upgrade.Add(new System.Net.Http.Headers.ProductHeaderValue("websocket"));
        request.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Protocol", WebSocketLineChannel.SubProtocol);
        using var response = await c.SendAsync(request);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, doc.RootElement.GetProperty("error").GetString());
    }

    private static async Task<TcpClient> SendUpgradeAsync(int port, string hostHeader, string? origin)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes(UpgradeRequest(hostHeader, origin)));
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static string UpgradeRequest(string hostHeader, string? origin)
    {
        var request = new StringBuilder()
            .Append("GET /ws HTTP/1.1\r\n")
            .Append("Host: ").Append(hostHeader).Append("\r\n")
            .Append("Upgrade: websocket\r\nConnection: Upgrade\r\n")
            .Append("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n")
            .Append("Sec-WebSocket-Protocol: ").Append(WebSocketLineChannel.SubProtocol).Append("\r\n");
        if (origin is not null)
            request.Append("Origin: ").Append(origin).Append("\r\n");
        request.Append("\r\n");
        return request.ToString();
    }
}
