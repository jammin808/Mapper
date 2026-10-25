using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net.Links;
using PlayMap.Net.Wire;

namespace PlayMap.Net.Tests;

/// <summary>A Patterns desk as the tests play it: STATE then NONCE on connect, HELLO answered OK, AUTH-PROOF checked
/// against the nonce and the HELLO name, plain AUTH checked against the token, PING answered with its stamps, STATUS answered with its
/// STATE and CONTEXT answered, VIDEO verbs answered with their id — and a test can push a STATE, fall silent, or cut the connection.
/// A desk older than proofs (or anything else at its address) sends no NONCE; a test can make the first NONCE any word.</summary>
internal sealed class FakePatternsDesk : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly Task _accept;
    private volatile StreamWriter? _current;
    private volatile TcpClient? _currentClient;
    private volatile string _nonce = "";

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

    /// <summary>The STATE said on connect, before the NONCE (null: <see cref="State"/>).</summary>
    public string? Greeting { get; set; }

    /// <summary>Whether the desk sends a NONCE on connect (a desk older than proofs sends none).</summary>
    public bool AsksProof { get; set; } = true;

    /// <summary>The first NONCE's word on a connection (null: a fresh 32-hex nonce); every later one is fresh.</summary>
    public string? FirstNonce { get; set; }

    public static string StateWith(double positionMs, long seq, bool playing, bool blackout) => string.Create(CultureInfo.InvariantCulture,
        $"{{\"blackout\":{(blackout ? "true" : "false")},\"video\":{{\"file\":\"sponsor.mp4\",\"tag\":\"VT\",\"position\":62,\"length\":210,\"playing\":{(playing ? "true" : "false")},\"ended\":false,\"loops\":false,\"positionMs\":{positionMs},\"lengthMs\":210000,\"remainingMs\":148000,\"paused\":false,\"rate\":1,\"seq\":{seq}}}}}");

    public async Task PushAsync(string state)
    {
        State = state;
        if (_current is { } w)
            await w.WriteLineAsync("STATE " + state);
    }

    /// <summary>Asks the connection up now for a proof again, with a fresh 32-hex NONCE.</summary>
    public async Task SendNonceAsync()
    {
        _nonce = Pairing.NewNonce();
        if (_current is { } w)
            await w.WriteLineAsync("NONCE " + _nonce);
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
            _nonce = FirstNonce ?? Pairing.NewNonce();
            var name = "";
            await writer.WriteLineAsync("STATE " + (Greeting ?? State));
            if (AsksProof)
                await writer.WriteLineAsync("NONCE " + _nonce);
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
                        var right = Pairing.Verify(Token, _nonce, name, words[1]);
                        _nonce = Pairing.NewNonce();
                        await writer.WriteLineAsync(right ? "OK" + tag + " paired" : "ERR" + tag + " wrong token — the pairing token is on the desk's Remote page, TRUST");
                        if (!right)
                            await writer.WriteLineAsync("NONCE " + _nonce);
                        break;
                    case "AUTH":
                        var plain = Pairing.Normalise(rest[5..]) == Pairing.Normalise(Token);
                        await writer.WriteLineAsync(plain ? "OK" + tag + " paired" : "ERR" + tag + " wrong token");
                        break;
                    case "STATUS":
                        await writer.WriteLineAsync("OK" + tag + " " + State);
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
/// its seq, the blackout followed, commands with ids, staleness and the redial; nothing it says taken and nothing sent to it before it
/// pairs; the token proved only against a 32-hex NONCE and sent plain only to this machine) and the desk's engine link against
/// PlayMap's own wire (pairing, STATE and PROJECT mirrored latest-wins, nothing queued while down).</summary>
public class LinkTests
{
    [Fact]
    public async Task ThePatternsLinkPairsTracksTheClipAndFollowsTheBlackout()
    {
        await using var desk = new FakePatternsDesk(TestSettings.Token);
        // The clip is counted, and the desk's silence too, on a clock only the test moves, so the count read is the one the test made
        // however late its next step runs.
        var clock = new ManualClock(1_000_000_000);
        await using var link = new PatternsLink(new PatternsLinkOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            Token = TestSettings.Token,
            ContextEvery = TimeSpan.FromMilliseconds(200),
            Clock = clock,
        }, TestSettings.Wall(), clock);
        var blackouts = new ConcurrentQueue<bool>();
        link.BlackoutChanged += blackouts.Enqueue;
        link.Start();

        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired));
        Assert.Contains("HELLO PlayMap", desk.Heard);
        Assert.DoesNotContain(desk.Heard, l => l.Contains(Pairing.Normalise(TestSettings.Token), StringComparison.Ordinal));
        Assert.True(await Eventually.TrueAsync(() => link.Video is not null));
        var first = link.Video!;
        Assert.Equal(("sponsor.mp4", 7L, 62000.0), (first.File, first.Seq, first.PositionMs));
        clock.AdvanceMs(300);
        Assert.Equal(62300, link.Video!.PositionMs, 6);
        Assert.False(link.Blackout);

        // A frame that agrees with the count (within a quarter second) keeps the anchor: no jump backwards.
        var counted = link.Video!.PositionMs;
        await desk.PushAsync(FakePatternsDesk.StateWith(counted - 120, 7, playing: true, blackout: true));
        Assert.True(await Eventually.TrueAsync(() => link.Blackout == true));
        Assert.Equal([false, true], blackouts);
        Assert.Equal(counted, link.Video!.PositionMs, 6);

        await desk.PushAsync(FakePatternsDesk.StateWith(10000, 8, playing: true, blackout: true));
        Assert.True(await Eventually.TrueAsync(() => link.Video is { Seq: 8 }));
        Assert.Equal(10000, link.Video!.PositionMs, 6);   // a new seq re-anchors

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
    public async Task NothingTheDeskSaysBeforeItPairsIsTakenAndItsStateIsAskedOncePaired()
    {
        // The desk greets with a STATE that blacks out, then asks for the proof: the greeting came before it paired, so its blackout
        // and its clip are set aside, and the STATE the desk answers once paired is the first one taken.
        await using var desk = new FakePatternsDesk(TestSettings.Token) { Greeting = FakePatternsDesk.StateWith(1000, 3, playing: false, blackout: true) };
        await using var link = new PatternsLink(new PatternsLinkOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            Token = TestSettings.Token,
            ContextEvery = TimeSpan.FromHours(1),
        }, TestSettings.Wall());
        var blackouts = new ConcurrentQueue<bool>();
        link.BlackoutChanged += blackouts.Enqueue;
        link.Start();

        Assert.True(await Eventually.TrueAsync(() => link.Video is { Seq: 7 }), $"{link.Pairing}, {link.SetAsideBeforePairing} set aside");
        Assert.Equal([false], blackouts);
        Assert.Equal(((bool?)false, 1L, true), (link.Blackout, link.SetAsideBeforePairing, link.Paired));
        var heard = desk.Heard.ToList();
        var proof = heard.FindIndex(l => l.StartsWith("@pair AUTH-PROOF ", StringComparison.Ordinal));
        var status = heard.FindIndex(l => l.StartsWith("@pm", StringComparison.Ordinal) && l.EndsWith(" STATUS", StringComparison.Ordinal));
        Assert.True(proof >= 0 && status > proof, string.Join(" | ", heard));
        Assert.Single(heard, l => l.EndsWith(" STATUS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeskThatAsksNoProofOffThisMachineIsNotFollowedAndHearsNoToken()
    {
        // Anything that answers at the desk's address and asks no proof — an older desk on another machine, or another machine at its
        // address — is never told the token, and blacks nothing out however its STATE reads.
        await using var desk = new FakePatternsDesk(TestSettings.Token)
        {
            AsksProof = false,
            Greeting = FakePatternsDesk.StateWith(1000, 3, playing: true, blackout: true),
        };
        // Its silence is counted on a clock only the test moves, so a slow runner never drops the link while the test watches it.
        await using var link = new PatternsLink(new PatternsLinkOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            Token = TestSettings.Token,
            IsLocal = _ => false,
            ContextEvery = TimeSpan.FromMilliseconds(100),
            Clock = new ManualClock(1_000_000_000),
        }, TestSettings.Wall());
        var blackouts = new ConcurrentQueue<bool>();
        link.BlackoutChanged += blackouts.Enqueue;
        link.Start();

        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.NotNeeded));
        await desk.PushAsync(FakePatternsDesk.StateWith(5000, 9, playing: true, blackout: true));
        Assert.True(await Eventually.TrueAsync(() => link.SetAsideBeforePairing == 2));
        var refused = await link.SendAsync("VIDEO PAUSE");
        // The context loop has turned three times, on a live link: none of its turns asked the desk anything.
        Assert.True(await Eventually.TrueAsync(() => link.ContextTurns >= 3), link.ContextTurns.ToString(CultureInfo.InvariantCulture));

        Assert.Equal((LinkStatus.Live, false, false), (link.Status, link.Local, link.Paired));
        Assert.Empty(blackouts);
        Assert.Null(link.Blackout);
        Assert.Null(link.Video);
        Assert.Null(link.ContextJson);
        Assert.Equal(0, link.Clock.Samples);
        Assert.False(refused.Ok);
        Assert.Contains("has not paired with this node — nothing is sent", refused.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(desk.Heard, l => l.Contains("AUTH", StringComparison.Ordinal) || l.Contains("VIDEO", StringComparison.Ordinal)
            || l.Contains("STATUS", StringComparison.Ordinal) || l.Contains("CONTEXT", StringComparison.Ordinal));
        Assert.DoesNotContain(desk.Heard, l => l.Contains(Pairing.Normalise(TestSettings.Token), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeskThatAsksNoProofOnThisMachinePairsByItsTokenAndIsFollowed()
    {
        await using var desk = new FakePatternsDesk(TestSettings.Token) { AsksProof = false };
        await using var link = new PatternsLink(new PatternsLinkOptions { Host = "127.0.0.1", Port = desk.Port, Token = TestSettings.Token }, TestSettings.Wall());
        link.Start();

        Assert.True(await Eventually.TrueAsync(() => link.Video is { Seq: 7 }), link.Pairing.ToString());
        Assert.Equal((PairState.Paired, true, 1L), (link.Pairing, link.Local, link.SetAsideBeforePairing));
        Assert.Single(desk.Heard, l => l == "@pair AUTH " + TestSettings.Token);
    }

    [Fact]
    public async Task AnOpenDeskIsFollowedWhenThisLinkHoldsNoToken()
    {
        await using var desk = new FakePatternsDesk("") { AsksProof = false };
        await using var link = new PatternsLink(new PatternsLinkOptions { Host = "127.0.0.1", Port = desk.Port, IsLocal = _ => false }, TestSettings.Wall());
        link.Start();

        Assert.True(await Eventually.TrueAsync(() => link.Video is { Seq: 7 }), link.Pairing.ToString());
        Assert.Equal((PairState.NotNeeded, true), (link.Pairing, link.Paired));
        Assert.DoesNotContain(desk.Heard, l => l.Contains("AUTH", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    public async Task OnlyA32HexNonceIsProvedAndNoNonceShapeGetsPlainAuth(string first)
    {
        // A NONCE of any other shape gets no proof, and a link that heard one sends no plain AUTH even to this machine; the desk's next
        // NONCE, of the right shape, gets the one proof.
        await using var desk = new FakePatternsDesk(TestSettings.Token) { FirstNonce = first };
        await using var link = new WireClient(new WireClientOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            ClientName = "PlayMap",
            Token = TestSettings.Token,
            NonceWait = TimeSpan.FromMilliseconds(100),
        }, TestSettings.Wall());
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Unprovable), link.Pairing.ToString());
        // The connection's wait for a NONCE has ended: no plain AUTH follows a NONCE of any shape (an AUTH written before that wait ended
        // would stand before the proof below in what the desk heard).
        Assert.True(await Eventually.TrueAsync(() => link.NonceWaitsEnded == 1), link.Pairing.ToString());
        Assert.DoesNotContain(desk.Heard, l => l.Contains("AUTH", StringComparison.Ordinal));
        Assert.True(link.Local);

        await desk.SendNonceAsync();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired), link.Pairing.ToString());
        Assert.Single(desk.Heard, l => l.Contains("AUTH", StringComparison.Ordinal));
        Assert.Single(desk.Heard, l => l.StartsWith("@pair AUTH-PROOF ", StringComparison.Ordinal));
        Assert.True(Pairing.IsNonce(Pairing.NewNonce()));
        Assert.False(Pairing.IsNonce(first));
    }

    [Fact]
    public async Task AClockEstimateKeptPerConnectionStartsEmptyOnEachNewOne()
    {
        // The Patterns link takes the desk's clock only from the connection the desk pairs on: what an earlier connection's answer said
        // is gone when the link dials again. Its silence and its pings are counted on a clock only the test moves (one ping per
        // connection, sent as it opens).
        await using var desk = new FakePatternsDesk(TestSettings.Token);
        await using var link = new WireClient(new WireClientOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            ClientName = "PlayMap",
            Token = TestSettings.Token,
            Clock = new ManualClock(1_000_000_000),
            ClockPerConnection = true,
        }, TestSettings.Wall());
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Clock.Samples == 1 && link.Pairing == PairState.Paired), link.Pairing.ToString());

        // The next connection's desk answers nothing: its estimate is empty, whatever the first one held.
        desk.Silent = true;
        desk.Cut();
        Assert.True(await Eventually.TrueAsync(() => link.Connections == 2 && desk.Accepted == 2 && link.Status == LinkStatus.Live),
            $"{link.Status} after {link.Connections} connections");
        Assert.True(await Eventually.TrueAsync(() => desk.Heard.Count(l => l.Contains(" PING ", StringComparison.Ordinal)) == 2));
        Assert.Equal(0, link.Clock.Samples);
    }

    [Fact]
    public async Task ThePatternsLinkGoesStaleOnSilenceDropsAndRedialsAndQueuesNothingWhileDown()
    {
        await using var desk = new FakePatternsDesk(TestSettings.Token);
        // The desk's silence is counted on a clock the test moves: the link goes stale and drops when the clock passes those marks,
        // whatever real time does (it says nothing between its pings, every 2 s, so on real time it would go stale and drop on its own).
        var clock = new ManualClock(1_000_000_000);
        await using var link = new PatternsLink(new PatternsLinkOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            Token = TestSettings.Token,
            ContextEvery = TimeSpan.FromHours(1),
            StaleAfter = TimeSpan.FromMilliseconds(400),
            DropAfter = TimeSpan.FromMilliseconds(900),
            Clock = clock,
        }, TestSettings.Wall());
        var statuses = new ConcurrentQueue<LinkStatus>();
        link.StatusChanged += statuses.Enqueue;
        // At the silent link's move to down (before it dials again): a line sent then, whether it was answered at once, and the desk
        // speaking again.
        var whileDown = new TaskCompletionSource<(bool AtOnce, Task<WireReply> Reply)>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.StatusChanged += s =>
        {
            if (s != LinkStatus.Down || !desk.Silent)
                return;
            var sent = link.SendAsync("VIDEO PLAY");
            desk.Silent = false;
            whileDown.TrySetResult((sent.IsCompleted, sent));
        };
        link.Start();
        // Live and paired by its proof, its first ping answered and the STATE it asks once paired taken (its clip): nothing more is due
        // from the desk, so no answer in flight lands after the clock moves.
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live && link.Pairing == PairState.Paired && link.Clock.Samples > 0
            && link.Video is not null), $"{link.Status}, {link.Pairing}");

        // The desk falls silent and the clock passes the stale mark: stale on the same connection, its clip not trusted.
        desk.Silent = true;
        clock.AdvanceMs(500);
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Stale), link.Status.ToString());
        Assert.Null(link.Video);
        Assert.Equal((1L, 1), (link.Connections, desk.Accepted));

        // Past the drop mark it drops: a line sent while it is down is refused at once, nothing queued.
        clock.AdvanceMs(500);
        var (atOnce, reply) = await whileDown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var refused = await reply;
        Assert.True(atOnce);
        Assert.False(refused.Ok);
        Assert.Contains("nothing is queued", refused.Text, StringComparison.Ordinal);

        // It dials again and is live and paired on the new connection.
        Assert.True(await Eventually.TrueAsync(() => statuses.Count >= 6 && link.Pairing == PairState.Paired, 8000), $"{string.Join(" ", statuses)}, {link.Pairing}");
        Assert.Equal([LinkStatus.Connecting, LinkStatus.Live, LinkStatus.Stale, LinkStatus.Down, LinkStatus.Connecting, LinkStatus.Live], statuses);
        Assert.Equal((LinkStatus.Live, 2L, 2), (link.Status, link.Connections, desk.Accepted));

        // A cut connection is dialled again, and a line sent on the new one once the desk paired there is answered.
        desk.Cut();
        Assert.True(await Eventually.TrueAsync(() => desk.Accepted == 3 && link.Connections == 3 && link.Status == LinkStatus.Live && link.Paired, 8000),
            $"{link.Status} after {link.Connections} connections, {link.Pairing}");
        Assert.True((await link.SendAsync("VIDEO PLAY")).Ok);
    }

    [Fact]
    public async Task ALinkCountsItsPeersSilenceOnItsClockAndIsLiveAgainWithTheNextLineOnTheSameConnection()
    {
        await using var desk = new FakePatternsDesk(TestSettings.Token);
        var clock = new ManualClock(1_000_000_000);
        await using var link = new WireClient(new WireClientOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            ClientName = "PlayMap",
            Token = TestSettings.Token,
            Clock = clock,
            StaleAfter = TimeSpan.FromMilliseconds(400),
            DropAfter = TimeSpan.FromMilliseconds(900),
        }, TestSettings.Wall());
        var statuses = new ConcurrentQueue<LinkStatus>();
        link.StatusChanged += statuses.Enqueue;
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live && link.Pairing == PairState.Paired && link.Clock.Samples > 0));

        // The peer falls silent for longer than the drop in real time while the link's clock stands still: nothing is counted.
        desk.Silent = true;
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Assert.Equal((LinkStatus.Live, 1L, TimeSpan.Zero), (link.Status, link.Connections, link.Silence));
        Assert.DoesNotContain(LinkStatus.Stale, statuses);

        // Past the stale mark on the clock it is stale, on the same connection; the peer's next line makes it live there again.
        clock.AdvanceMs(500);
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Stale), link.Status.ToString());
        Assert.Equal((1L, TimeSpan.FromMilliseconds(500)), (link.Connections, link.Silence));
        await desk.PushAsync(desk.State);
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live), link.Status.ToString());
        Assert.Equal((1L, TimeSpan.Zero, 1), (link.Connections, link.Silence, desk.Accepted));

        // Past the drop mark on the clock it drops and dials again.
        clock.AdvanceMs(1000);
        Assert.True(await Eventually.TrueAsync(() => link.Connections == 2 && desk.Accepted == 2 && link.Status == LinkStatus.Live, 8000),
            $"{link.Status} after {link.Connections} connections");
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
