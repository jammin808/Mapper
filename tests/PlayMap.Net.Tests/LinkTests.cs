using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PlayMap.Core.Bus;
using PlayMap.Core.Wire;
using PlayMap.Net.Links;
using PlayMap.Net.Wire;

namespace PlayMap.Net.Tests;

/// <summary>A Patterns desk as the tests play it: STATE then NONCE on connect, HELLO answered OK, AUTH-PROOF checked
/// against the nonce and the HELLO name, PING answered with its stamps, CONTEXT answered, VIDEO verbs answered with
/// their id — and a test can push a STATE, fall silent, or cut the connection.</summary>
internal sealed class FakePatternsDesk : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly Task _accept;
    private volatile StreamWriter? _current;
    private volatile TcpClient? _currentClient;

    public FakePatternsDesk(string token)
    {
        Token = token;
        _listener.Start();
        _accept = Task.Run(AcceptAsync, CancellationToken.None);
    }

    public string Token { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Accepted;

    public volatile bool Silent;

    public ConcurrentQueue<string> Heard { get; } = new();

    public string State { get; set; } = StateWith(62000, 7, playing: true, blackout: false);

    public static string StateWith(double positionMs, long seq, bool playing, bool blackout) => string.Create(CultureInfo.InvariantCulture,
        $"{{\"blackout\":{(blackout ? "true" : "false")},\"video\":{{\"file\":\"sponsor.mp4\",\"tag\":\"VT\",\"position\":62,\"length\":210,\"playing\":{(playing ? "true" : "false")},\"ended\":false,\"loops\":false,\"positionMs\":{positionMs},\"lengthMs\":210000,\"remainingMs\":148000,\"paused\":false,\"rate\":1,\"seq\":{seq}}}}}");

    public async Task PushAsync(string state)
    {
        State = state;
        if (_current is { } w)
            await w.WriteLineAsync("STATE " + state);
    }

    /// <summary>Cuts the current connection (the link should redial).</summary>
    public void Cut() => _currentClient?.Client.Close(0);

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _clients.Add(client);
            Interlocked.Increment(ref Accepted);
            _ = Task.Run(() => ServeAsync(client), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _current = writer;
            _currentClient = client;
            var nonce = Pairing.NewNonce();
            var name = "";
            await writer.WriteLineAsync("STATE " + State);
            await writer.WriteLineAsync("NONCE " + nonce);
            while (await reader.ReadLineAsync() is { } line)
            {
                Heard.Enqueue(line);
                if (Silent)
                    continue;
                var (id, rest) = line.StartsWith('@') ? (line[1..line.IndexOf(' ', StringComparison.Ordinal)], line[(line.IndexOf(' ', StringComparison.Ordinal) + 1)..]) : ("", line);
                var tag = id.Length > 0 ? " @" + id : "";
                var words = rest.Split(' ');
                switch (words[0])
                {
                    case "HELLO":
                        name = rest[6..];
                        await writer.WriteLineAsync("OK" + tag);
                        break;
                    case "AUTH-PROOF":
                        var right = Pairing.Verify(Token, nonce, name, words[1]);
                        nonce = Pairing.NewNonce();
                        await writer.WriteLineAsync(right ? "OK" + tag + " paired" : "ERR" + tag + " wrong token — the pairing token is on the desk's Remote page, TRUST");
                        if (!right)
                            await writer.WriteLineAsync("NONCE " + nonce);
                        break;
                    case "PING":
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                        await writer.WriteLineAsync("OK" + tag + " PONG " + words[1] + " " + now + " " + now);
                        break;
                    case "CONTEXT":
                        await writer.WriteLineAsync("OK" + tag + " {\"v\":1,\"node\":{\"t\":\"patterns\",\"id\":\"9f8e7d6c\",\"name\":\"FOH\"},\"at\":1,\"light\":\"green\",\"headline\":\"Programme: VT1\",\"items\":[],\"links\":[],\"recent\":[],\"commands\":[],\"peers\":[]}");
                        break;
                    case "VIDEO":
                        await writer.WriteLineAsync("OK" + tag);
                        break;
                    default:
                        await writer.WriteLineAsync("ERR" + tag + " unknown command");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // The link went away; the next connection is served afresh.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        foreach (var c in _clients)
            c.Dispose();
        await _accept;
        _stop.Dispose();
    }
}

/// <summary>The links PlayMap dials: a Patterns desk (pairing by proof, the clip clock counted on and re-anchored on
/// its seq, the blackout followed, commands with ids, staleness and the redial) and the desk's engine link against
/// PlayMap's own wire (pairing, STATE and PROJECT mirrored latest-wins, nothing queued while down).</summary>
public class LinkTests
{
    [Fact]
    public async Task ThePatternsLinkPairsTracksTheClipAndFollowsTheBlackout()
    {
        await using var desk = new FakePatternsDesk(TestSettings.Token);
        await using var link = new PatternsLink(new PatternsLinkOptions { Host = "127.0.0.1", Port = desk.Port, Token = TestSettings.Token, ContextEvery = TimeSpan.FromMilliseconds(200) }, TestSettings.Wall());
        var blackouts = new ConcurrentQueue<bool>();
        link.BlackoutChanged += blackouts.Enqueue;
        link.Start();

        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired));
        Assert.Contains("HELLO PlayMap", desk.Heard);
        Assert.DoesNotContain(desk.Heard, l => l.Contains(Pairing.Normalise(TestSettings.Token), StringComparison.Ordinal));
        Assert.True(await Eventually.TrueAsync(() => link.Video is not null));
        var first = link.Video!;
        Assert.Equal("sponsor.mp4", first.File);
        Assert.Equal(7, first.Seq);
        await Task.Delay(300);
        Assert.InRange(link.Video!.PositionMs, 62250, 64000);
        Assert.False(link.Blackout);

        // A frame that agrees with the count (within a quarter second) keeps the anchor: no jump backwards.
        var counted = link.Video!.PositionMs;
        await desk.PushAsync(FakePatternsDesk.StateWith(counted - 120, 7, playing: true, blackout: true));
        Assert.True(await Eventually.TrueAsync(() => link.Blackout == true));
        Assert.Equal([false, true], blackouts);
        Assert.True(link.Video!.PositionMs >= counted);

        await desk.PushAsync(FakePatternsDesk.StateWith(10000, 8, playing: true, blackout: true));
        Assert.True(await Eventually.TrueAsync(() => link.Video is { Seq: 8 }));
        Assert.InRange(link.Video!.PositionMs, 10000, 11000);   // a new seq re-anchors

        var reply = await link.SendAsync("VIDEO PAUSE");
        Assert.True(reply.Ok);
        Assert.Contains(desk.Heard, l => l.StartsWith("@pm", StringComparison.Ordinal) && l.EndsWith(" VIDEO PAUSE", StringComparison.Ordinal));
        Assert.True(await Eventually.TrueAsync(() => link.Clock.Samples > 0));
        Assert.InRange(link.Clock.OffsetMs, -1000, 1000);
        Assert.True(await Eventually.TrueAsync(() => link.ContextJson is not null));
        Assert.Contains("Programme: VT1", link.ContextJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongTokenIsRefusedOnceAndNotRetriedOnThatConnection()
    {
        await using var desk = new FakePatternsDesk(TestSettings.Token);
        await using var link = new PatternsLink(new PatternsLinkOptions { Host = "127.0.0.1", Port = desk.Port, Token = "AAAA-BBBB-CCCC" }, TestSettings.Wall());
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Refused));
        await Task.Delay(200);
        Assert.Single(desk.Heard, l => l.Contains("AUTH-PROOF", StringComparison.Ordinal));
        Assert.False(link.Paired);
    }

    [Fact]
    public async Task ThePatternsLinkGoesStaleOnSilenceDropsAndRedialsAndQueuesNothingWhileDown()
    {
        await using var desk = new FakePatternsDesk(TestSettings.Token);
        await using var link = new PatternsLink(new PatternsLinkOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            Token = TestSettings.Token,
            StaleAfter = TimeSpan.FromMilliseconds(400),
            DropAfter = TimeSpan.FromMilliseconds(900),
        }, TestSettings.Wall());
        var statuses = new ConcurrentQueue<LinkStatus>();
        link.StatusChanged += statuses.Enqueue;
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live && link.Paired));

        desk.Silent = true;
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Stale));
        Assert.Null(link.Video);   // a stale link's clip is not trusted
        Assert.True(await Eventually.TrueAsync(() => link.Status is LinkStatus.Down or LinkStatus.Connecting));
        var refused = await link.SendAsync("VIDEO PLAY");
        Assert.False(refused.Ok);
        Assert.Contains("nothing is queued", refused.Text, StringComparison.Ordinal);

        desk.Silent = false;
        Assert.True(await Eventually.TrueAsync(() => desk.Accepted >= 2 && link.Status == LinkStatus.Live && link.Paired, 8000));
        Assert.Contains(LinkStatus.Stale, statuses);

        desk.Cut();
        Assert.True(await Eventually.TrueAsync(() => desk.Accepted >= 3 && link.Status == LinkStatus.Live, 8000));
        Assert.True((await link.SendAsync("VIDEO PLAY")).Ok);
    }

    [Fact]
    public void ATypedPatternsAddressReadsHostAndPort()
    {
        Assert.Equal(("10.0.0.5", 9697), PatternsLinkOptions.ParseAddress("10.0.0.5"));
        Assert.Equal(("foh-pc", 9800), PatternsLinkOptions.ParseAddress(" foh-pc:9800 "));
        Assert.Equal(("fe80::1", 9697), PatternsLinkOptions.ParseAddress("[fe80::1]:9697"));
        Assert.Null(PatternsLinkOptions.ParseAddress("http://foh"));
        Assert.Null(PatternsLinkOptions.ParseAddress("foh:99999"));
    }

    [Fact]
    public async Task TheEngineLinkPairsMirrorsStateAndProjectAndRedials()
    {
        var endpoint = new FakeEndpoint();
        var settings = TestSettings.Make(trustLoopback: false);
        var server = new WireServer(settings, endpoint, TestSettings.Wall(), new WireServerOptions { PlainAuthOnLoopback = false });
        server.Start();
        var port = server.Port;
        await using var link = new EngineLinkClient("127.0.0.1", port, TestSettings.Token);
        var projects = new ConcurrentQueue<long>();
        link.ProjectChanged += projects.Enqueue;
        try
        {
            link.Start();
            Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired && link.Project.Revision == 1));
            Assert.Equal("a1b2c3d4", link.NodeId);
            Assert.Equal("Stage left", link.NodeName);
            Assert.Equal("{\"name\":\"Show\"}", link.Project.Json);
            Assert.Equal(1, link.State.Revision);

            var reply = await link.SendAsync("BLACKOUT ON");
            Assert.True(reply.Ok, reply.Text);
            Assert.True(endpoint.Blackout);
            Assert.Equal("desk", endpoint.Lines.Single().Session.Transport);
            Assert.True(await Eventually.TrueAsync(() => link.State.Revision == 2 && link.State.Json.Contains("\"blackout\":true", StringComparison.Ordinal)));

            endpoint.RenameProject("Tour");
            Assert.True(await Eventually.TrueAsync(() => link.Project.Revision == 2));
            Assert.Equal("{\"name\":\"Tour\"}", link.Project.Json);
            Assert.Equal(2, projects.Last());

            Assert.False((await link.SendAsync("BLACKOUT ON")).Ok);   // refused by the node: already blacked out
        }
        finally
        {
            await server.DisposeAsync();
        }
        Assert.True(await Eventually.TrueAsync(() => link.Status is LinkStatus.Down or LinkStatus.Connecting));
        Assert.False((await link.SendAsync("BLACKOUT OFF")).Ok);

        await using var again = new WireServer(settings, endpoint, TestSettings.Wall(), new WireServerOptions { Port = port, PlainAuthOnLoopback = false });
        again.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live && link.Pairing == PairState.Paired, 10000));
        Assert.True((await link.SendAsync("BLACKOUT OFF")).Ok);
    }

    [Fact]
    public async Task ASecondListenerOnAPortInUseStaysClosed()
    {
        await using var first = new WireServer(TestSettings.Make(), new FakeEndpoint(), TestSettings.Wall(), new WireServerOptions { Port = 0 });
        first.Start();
        await using var second = new WireServer(TestSettings.Make(), new FakeEndpoint(), TestSettings.Wall(), new WireServerOptions { Port = first.Port });
        Assert.Throws<BindRefusedException>(second.Start);
    }
}
