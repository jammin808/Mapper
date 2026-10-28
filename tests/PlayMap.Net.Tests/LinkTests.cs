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
/// A desk older than proofs (or anything else at its address) sends no NONCE; a test can make the first NONCE any word, leave one verb
/// unanswered, answer each PING with a t1 it never sent, or close each connection once it has greeted it.</summary>
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

    /// <summary>A verb the desk reads and never answers (null: every verb is answered).</summary>
    public string? Unanswered { get; set; }

    /// <summary>Each PING is answered with a t1 a second before the one it carried: a pong for a ping never sent, which the clock's
    /// admission alone would take as a sample.</summary>
    public volatile bool ForgedPongs;

    /// <summary>Each connection is closed once the desk has greeted it (its STATE and NONCE): a peer that answers and closes.</summary>
    public volatile bool CloseOnGreeting;

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
            if (CloseOnGreeting)
            {
                client.Client.Close(0);
                return;
            }
            while (await reader.ReadLineAsync() is { } line)
            {
                Heard.Enqueue(line);
                if (Silent)
                    continue;
                var (id, rest) = line.StartsWith('@') ? (line[1..line.IndexOf(' ', StringComparison.Ordinal)], line[(line.IndexOf(' ', StringComparison.Ordinal) + 1)..]) : ("", line);
                var tag = id.Length > 0 ? " @" + id : "";
                var words = rest.Split(' ');
                if (words[0] == Unanswered)
                    continue;
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
                        var t1 = ForgedPongs ? (long.Parse(words[1], CultureInfo.InvariantCulture) - 1000).ToString(CultureInfo.InvariantCulture) : words[1];
                        await writer.WriteLineAsync("OK" + tag + " PONG " + t1 + " " + now + " " + now);
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

    /// <summary>Show Bus 1.3 §13.12: every command carries <c>&lt;prefix&gt;&lt;n&gt;-&lt;inst&gt;</c>, a counter from 1 and the process's inst, and
    /// ends in one of 13.12.2's outcomes. An answer applies or refuses; a command written that no answer settles within the reply
    /// timeout — on the link's clock, an answer at the limit still counting — or whose connection ends first is unconfirmed, never
    /// failed (it may have run), its reply saying so; one never written is not-sent. Nothing is sent again.</summary>
    [Fact]
    public async Task ACommandCarriesARunUniqueIdAndOneWrittenWithNoAnswerIsUnconfirmedNeverFailed()
    {
        await using var desk = new FakePatternsDesk(TestSettings.Token) { Unanswered = "VIDEO" };
        var clock = new ManualClock(1_000_000_000);
        await using var link = new WireClient(new WireClientOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            ClientName = "PlayMap",
            Token = TestSettings.Token,
            IdPrefix = "pm",
            Inst = "a41f09c2",
            Clock = clock,
            // Only the reply's deadline is moved here: the peer's silence never drops the link while the clock moves past it.
            StaleAfter = TimeSpan.FromHours(1),
            DropAfter = TimeSpan.FromHours(2),
        }, TestSettings.Wall());
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired), link.Pairing.ToString());

        var status = await link.CommandAsync("STATUS");
        Assert.Equal(("pm1-a41f09c2", PeerOutcome.Applied, true), (status.Id, status.Outcome, status.Reply.Ok));
        Assert.Equal(desk.State, status.Reply.Text);
        var refused = await link.CommandAsync("BOGUS");
        Assert.Equal(("pm2-a41f09c2", PeerOutcome.Refused, "unknown command", false), (refused.Id, refused.Outcome, refused.Words, refused.Reply.Ok));

        var pause = link.CommandAsync("VIDEO PAUSE");
        Assert.True(await Eventually.TrueAsync(() => desk.Heard.Contains("@pm3-a41f09c2 VIDEO PAUSE")), string.Join(" | ", desk.Heard));
        clock.AdvanceMs(30_000);
        await Task.Delay(400);   // more than one of the beat's steps: an answer at the limit still counts
        Assert.False(pause.IsCompleted);
        clock.AdvanceMs(1);
        var unanswered = await pause.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((PeerOutcome.Unconfirmed, false), (unanswered.Outcome, unanswered.Reply.Ok));
        Assert.Equal($"no answer from 127.0.0.1:{desk.Port} in 30 s — it may have run; look at its state", unanswered.Words);
        Assert.Equal(WireOutcome.UnconfirmedWord + unanswered.Words, unanswered.Reply.Text);

        var play = link.CommandAsync("VIDEO PLAY");
        Assert.True(await Eventually.TrueAsync(() => desk.Heard.Contains("@pm4-a41f09c2 VIDEO PLAY")), string.Join(" | ", desk.Heard));
        desk.Cut();
        var dropped = await play.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PeerOutcome.Unconfirmed, dropped.Outcome);
        Assert.Equal($"the link to 127.0.0.1:{desk.Port} dropped before it answered — it may have run; look at its state", dropped.Words);

        var notSent = await link.CommandAsync("@x VIDEO PLAY");
        Assert.Equal((null, PeerOutcome.NotSent, false), (notSent.Id, notSent.Outcome, notSent.Reply.Ok));
        // Nothing is sent again: each command reached the desk once, on the connection it was written on.
        Assert.Single(desk.Heard, l => l.EndsWith(" VIDEO PAUSE", StringComparison.Ordinal));
        Assert.Single(desk.Heard, l => l.EndsWith(" VIDEO PLAY", StringComparison.Ordinal));
    }

    /// <summary>Show Bus 1.3 §13.15: a redial waits 13.15.1's wait for the dials that failed since the link was last stable. A desk that
    /// greets and closes says something on every connection, and still each redial waits longer; only a connection whose pairing
    /// settled and that stayed up <c>peerlink.stable.ms</c> on the link's clock starts the wait over, and after it each failed dial
    /// doubles the next again. Each wait is read as the link computes it, so real time decides nothing here.</summary>
    [Fact]
    public async Task ARedialWaitsLongerForEachFailedDialUntilASettledConnectionStaysUpTenSeconds()
    {
        await using var desk = new FakePatternsDesk(TestSettings.Token) { CloseOnGreeting = true };
        var clock = new ManualClock(1_000_000_000);
        var waits = new ConcurrentQueue<(int N, TimeSpan Wait)>();
        await using var link = new WireClient(new WireClientOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            ClientName = "PlayMap",
            Token = TestSettings.Token,
            Clock = clock,
        }, TestSettings.Wall());
        link.Redialling += (n, wait) => waits.Enqueue((n, wait));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => waits.Count >= 3, 10_000), $"{desk.Accepted} dials");
        // 400–600 ms, then 800–1200, then 1600–2400: each failed dial doubles the next wait.
        Assert.Equal(new[] { 0, 1, 2 }, waits.Take(3).Select(w => w.N));
        Assert.All(waits, w => StagewatchLinkTests.InRedialWait(w.N, w.Wait));

        // The desk stays now: the connection pairs, and once it has been up 10 s on the link's clock the next wait starts over.
        desk.CloseOnGreeting = false;
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired && link.Status == LinkStatus.Live, 15_000), link.Pairing.ToString());
        Assert.False(link.Stable);
        clock.AdvanceMs(9_999);
        await Task.Delay(400);
        Assert.False(link.Stable);
        clock.AdvanceMs(1);
        Assert.True(await Eventually.TrueAsync(() => link.Stable), "the settled connection was never marked stable");

        // The stable connection is cut, and the desk greets and closes again: the first wait is the first, then each doubles.
        var before = waits.Count;
        desk.CloseOnGreeting = true;
        desk.Cut();
        Assert.True(await Eventually.TrueAsync(() => waits.Count >= before + 3, 15_000), $"{waits.Count - before} redials");
        var after = waits.Skip(before).Take(3).ToList();
        Assert.Equal(new[] { 0, 1, 2 }, after.Select(w => w.N));
        Assert.All(after, w => StagewatchLinkTests.InRedialWait(w.N, w.Wait));
    }

    /// <summary>A peer that asks no proof settles the pairing of a link that holds no token, which a connection up 10 s then makes stable;
    /// of a link that holds one it settles nothing — anything at the peer's address could answer so — and no time up makes it stable.</summary>
    [Theory]
    [InlineData("", true)]
    [InlineData(TestSettings.Token, false)]
    public async Task APeerThatAsksNoProofSettlesOnlyALinkThatHoldsNoToken(string token, bool stable)
    {
        await using var desk = new FakePatternsDesk(token) { AsksProof = false };
        var clock = new ManualClock(1_000_000_000);
        await using var link = new WireClient(new WireClientOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            ClientName = "PlayMap",
            Token = token,
            IsLocal = _ => false,
            Clock = clock,
            StaleAfter = TimeSpan.FromHours(1),
            DropAfter = TimeSpan.FromHours(2),
        }, TestSettings.Wall());
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.NotNeeded), link.Pairing.ToString());
        clock.AdvanceMs(10_000);
        if (stable)
            Assert.True(await Eventually.TrueAsync(() => link.Stable), "the open peer's connection was never marked stable");
        else
        {
            await Task.Delay(400);   // more than one of the beat's steps
            Assert.False(link.Stable);
        }
        Assert.Equal(1, link.Connections);
    }

    /// <summary>Show Bus 1.3 §13.12.1: a request id is unique for the process. Links that number with one prefix and inst — made one after
    /// another, or up side by side — share one counter, so none writes an id another wrote.</summary>
    [Fact]
    public async Task LinksWithOnePrefixAndInstNeverWriteOneIdTwice()
    {
        await using var desk = new FakePatternsDesk(TestSettings.Token);
        WireClient Link() => new(new WireClientOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            ClientName = "PlayMap",
            Token = TestSettings.Token,
            IdPrefix = "pm",
            Inst = "b52c0e19",
        }, TestSettings.Wall());
        async Task<string?> StatusAsync(WireClient link)
        {
            Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired), link.Pairing.ToString());
            return (await link.CommandAsync("STATUS")).Id;
        }

        var ids = new List<string?>();
        await using (var first = Link())
        {
            first.Start();
            ids.Add(await StatusAsync(first));
        }
        // Two up side by side (each pairs before the next dials: the desk keeps one nonce at a time).
        await using var second = Link();
        second.Start();
        Assert.True(await Eventually.TrueAsync(() => second.Pairing == PairState.Paired), second.Pairing.ToString());
        await using var third = Link();
        third.Start();
        ids.Add(await StatusAsync(second));
        ids.Add(await StatusAsync(third));
        ids.Add(await StatusAsync(second));
        Assert.Equal(new[] { "pm1-b52c0e19", "pm2-b52c0e19", "pm3-b52c0e19", "pm4-b52c0e19" }, ids);
        Assert.Equal(4, desk.Heard.Count(l => l.EndsWith(" STATUS", StringComparison.Ordinal)));
    }

    /// <summary>Show Bus 1.3 §13.12.3: an answer is read by the table, and the reply a reader of OK and ERR takes says the same — a refusal
    /// an OK carries (a refused command's repeat) is Ok false with the refusal's words; an answer that applied keeps its line.</summary>
    [Fact]
    public void AnOkThatCarriesARefusalIsARefusedReply()
    {
        var repeat = WireOutcome.Answered("pm9-a41f09c2", "OK @pm9-a41f09c2 {\"ok\":false,\"error\":\"already blacked out\",\"repeat\":true}");
        Assert.Equal((PeerOutcome.Refused, false), (repeat.Outcome, repeat.Reply.Ok));
        Assert.Equal(repeat.Words, repeat.Reply.Text);
        Assert.StartsWith("already blacked out", repeat.Words, StringComparison.Ordinal);

        var applied = WireOutcome.Answered("pm10-a41f09c2", "OK @pm10-a41f09c2 blackout on");
        Assert.Equal((PeerOutcome.Applied, true, "blackout on"), (applied.Outcome, applied.Reply.Ok, applied.Reply.Text));
        var refused = WireOutcome.Answered("pm11-a41f09c2", "ERR @pm11-a41f09c2 not blacked out");
        Assert.Equal((PeerOutcome.Refused, false, "not blacked out"), (refused.Outcome, refused.Reply.Ok, refused.Reply.Text));
    }

    /// <summary>The reply carries its command's outcome (Show Bus 1.3 §13.12.2) to the desk's engine link and every reader past it: an
    /// applied answer and a refusal as they came, a command the engine never answered (its connection ended first) unconfirmed — Ok false,
    /// never a refusal, its words that it may have run and to look at the engine's state — and one never written not-sent; and every
    /// outcome the line wire's client makes, an answer 13.12.3's table reads as accepted or a refusal an OK carries among them, is its
    /// reply's, so a reader of the reply and a reader of the outcome never disagree. Nothing is sent again.</summary>
    [Fact]
    public async Task TheReplyCarriesItsOutcomeToTheDesksEngineLinkAndAnUnansweredCommandIsUnconfirmedNeverRefused()
    {
        await using var engine = new FakePatternsDesk(TestSettings.Token) { Unanswered = "VIDEO" };
        await using var link = new EngineLinkClient("127.0.0.1", engine.Port, TestSettings.Token, TestSettings.Wall());
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.Paired && link.Status == LinkStatus.Live), link.Pairing.ToString());

        var status = await link.SendAsync("STATUS");
        Assert.Equal((true, PeerOutcome.Applied, false), (status.Ok, status.Outcome, status.IsUnconfirmed));
        var refused = await link.SendAsync("BOGUS");
        Assert.Equal((false, PeerOutcome.Refused, "unknown command"), (refused.Ok, refused.Outcome, refused.Text));

        var play = link.SendAsync("VIDEO PLAY");
        Assert.True(await Eventually.TrueAsync(() => engine.Heard.Any(l => l.EndsWith(" VIDEO PLAY", StringComparison.Ordinal))), string.Join(" | ", engine.Heard));
        engine.Cut();
        var unanswered = await play.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((false, PeerOutcome.Unconfirmed, true), (unanswered.Ok, unanswered.Outcome, unanswered.IsUnconfirmed));
        Assert.Equal($"unconfirmed: the link to 127.0.0.1:{engine.Port} dropped before it answered — {WireReply.MayHaveRunWords}", unanswered.Text);

        var notSent = await link.SendAsync("@d1 VIDEO PLAY");
        Assert.Equal((false, PeerOutcome.NotSent), (notSent.Ok, notSent.Outcome));
        Assert.Single(engine.Heard, l => l.EndsWith(" VIDEO PLAY", StringComparison.Ordinal));

        var made = new[]
        {
            WireOutcome.Answered("d7-a41f09c2", "OK @d7-a41f09c2 {\"ok\":true,\"stage\":\"accepted\",\"effect\":\"scan queued\"}"),
            WireOutcome.Answered("d8-a41f09c2", "OK @d8-a41f09c2 {\"ok\":false,\"error\":\"already blacked out\",\"repeat\":true}"),
            WireOutcome.Answered("d9-a41f09c2", "OK @d9-a41f09c2 blackout on"),
            WireOutcome.Answered("d10-a41f09c2", "ERR @d10-a41f09c2 not blacked out"),
            WireOutcome.Unconfirmed("d11-a41f09c2", "no answer from 127.0.0.1:9700 in 30 s — " + WireReply.MayHaveRunWords),
            WireOutcome.NotSent(null, "the link to 127.0.0.1:9700 is down — nothing is queued"),
        };
        Assert.Equal(Enum.GetValues<PeerOutcome>().Order(), made.Select(m => m.Outcome).Distinct().Order());
        Assert.All(made, m => Assert.Equal((m.Outcome, m.Outcome is PeerOutcome.Applied or PeerOutcome.Accepted), (m.Reply.Outcome, m.Reply.Ok)));
    }

    /// <summary>Show Bus 1.3 §13.10.2: a pong is a clock sample only when it answers one of the last pings — its id and its t1. A desk
    /// that answers with a t1 never sent gives no sample, and each such pong is counted.</summary>
    [Fact]
    public async Task APongIsAClockSampleOnlyWhenItAnswersOneOfTheLastPings()
    {
        await using var desk = new FakePatternsDesk(TestSettings.Token) { ForgedPongs = true };
        var clock = new ManualClock(1_000_000_000);
        await using var link = new WireClient(new WireClientOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            ClientName = "PlayMap",
            Token = TestSettings.Token,
            Clock = clock,
        }, TestSettings.Wall());
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.UnmatchedPongs >= 1), link.Pairing.ToString());
        Assert.Equal(0, link.Clock.Samples);

        desk.ForgedPongs = false;
        clock.AdvanceMs(2_000);
        Assert.True(await Eventually.TrueAsync(() => link.Clock.Samples == 1), $"{link.Clock.Samples} samples");
        Assert.Equal(1, link.UnmatchedPongs);
    }

    /// <summary>Show Bus 1.3 §13.6.8: §7's proof keys a token as 1.1 desks do, and a token that still holds a character they read apart
    /// once their dashes and spaces are dropped is never proved: the link says to retype it, and nothing is written for it.</summary>
    [Theory]
    [InlineData("K7QM\u00a03XWD-P9RA")]
    [InlineData("\ufeffK7QM-3XWD-P9RA")]
    public async Task ATokenADeskReadsApartIsNeverProved(string token)
    {
        await using var desk = new FakePatternsDesk(TestSettings.Token);
        await using var link = new WireClient(new WireClientOptions
        {
            Host = "127.0.0.1",
            Port = desk.Port,
            ClientName = "PlayMap",
            Token = token,
        }, TestSettings.Wall());
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Pairing == PairState.TokenUnprovable), link.Pairing.ToString());
        await Task.Delay(300);
        Assert.DoesNotContain(desk.Heard, l => l.Contains("AUTH", StringComparison.Ordinal));
        Assert.Equal(PairState.TokenUnprovable, link.Pairing);
    }
}
