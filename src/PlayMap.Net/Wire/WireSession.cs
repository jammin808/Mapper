using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Wire;
using PlayMap.Net.Commands;
using PlayMap.Net.Companion;

namespace PlayMap.Net.Wire;

/// <summary>One connection's life on the line wire (docs/WIRE.md "A connection's life"). Lines are read and run
/// one at a time, so replies leave in the order their lines came; the session's writer is its only voice.
/// Pairing is the connection's: a proof answers this connection's nonce, a nonce answers one proof, and the token
/// a proof matched is compared with the node's token on every verb (a new token unpairs at once). On the web port's line
/// channel (a WebSocket) nothing but NONCE, HELLO, AUTH-PROOF and PING is answered until it pairs, the greeting STATE waits
/// for "OK paired", plain AUTH is never taken, and wrong proofs count against the address in the web port's ledger.</summary>
internal sealed class WireSession : IAsyncDisposable
{
    private static readonly TimeSpan LastWordWait = TimeSpan.FromSeconds(2);

    /// <summary>The desk's HELLO name: the desk role needs more than the name (<see cref="IsDesk"/>).</summary>
    public const string DeskName = "PlayMapDesk";

    /// <summary>The verbs a WebSocket line channel answers before it pairs (plain AUTH only to say it takes proofs): everything else
    /// waits for the proof.</summary>
    private static readonly HashSet<string> BeforePairing = new(StringComparer.Ordinal) { "NONCE", "HELLO", "AUTH-PROOF", "AUTH", "PING" };

    private readonly LineSessions _server;
    private readonly ILineChannel _channel;
    private readonly IPAddress? _remote;
    private readonly string _address;
    private readonly bool _loopback;
    private readonly string _id;
    private readonly bool _strict;
    private string _nonce = Pairing.NewNonce();
    private string _helloName = "";
    private bool _saidHello;
    private bool _greetOnPairing;
    private bool _registered;
    private string? _pairedToken;
    private int _wrong;
    private bool _close;
    private int _closeCode = 1000;
    private volatile bool _wantsState;
    private volatile bool _wantsContext;
    private volatile bool _wantsProject;
    private readonly TopicSubscriptions _topics = new();

    public WireSession(LineSessions server, ILineChannel channel, string id)
    {
        _server = server;
        _channel = channel;
        _remote = channel.Remote;
        _address = NetAddress.Text(_remote);
        _loopback = NetAddress.IsLoopback(_remote);
        _id = id;
        _strict = channel.Kind == "ws";
        var limits = server.Options.Limits;
        Writer = new LineWriter(channel.Output, limits.MaxQueuedReplies, TimeSpan.FromSeconds(limits.WriteSeconds), channel.Abort);
    }

    public LineWriter Writer { get; }

    public bool WantsState => _wantsState;

    public bool WantsContext => _wantsContext;

    public bool WantsProject => _wantsProject;

    /// <summary>May run a mutating verb now: the node asks no token, the peer is trusted, or the token it proved is
    /// the node's token now.</summary>
    private bool Paired
    {
        get
        {
            var settings = _server.Settings;
            var token = Pairing.Normalise(settings.Token);
            return token.Length == 0
                || NetAddress.IsTrusted(_remote, settings)
                || (_pairedToken is not null && string.Equals(_pairedToken, token, StringComparison.Ordinal));
        }
    }

    /// <summary>Who this is for the command router and the journal. A name is a claim, and a claim only narrows: "Companion:…"
    /// is a controller; "Stagewatch" is Stagewatch's VT link; an address on the trusted list that never said hello is a Patterns
    /// "Lines" device; "PlayMapDesk" is the desk only when <see cref="IsDesk"/> holds; anything else is the door's own transport.</summary>
    private SessionInfo Info()
    {
        var transport = _helloName switch
        {
            DeskName when IsDesk() => "desk",
            "Stagewatch" => "stagewatch",
            var n when IsCompanionName(n) => "companion",
            _ when !_saidHello && NetAddress.IsListed(_remote, _server.Settings) => "patterns-device",
            _ => _strict ? "ws" : "wire",
        };
        return new SessionInfo(_id, transport, _address, _loopback, _helloName, Paired);
    }

    /// <summary>"Companion" or "Companion:&lt;label&gt;".</summary>
    public static bool IsCompanionName(string name) =>
        name == "Companion" || name.StartsWith("Companion:", StringComparison.Ordinal);

    /// <summary>The desk is the operator, so its role comes from what a client cannot simply claim: the HELLO name on the TCP wire
    /// (never the web port), and the node's token proved on this connection (AUTH-PROOF, or plain AUTH from this machine) — trust
    /// of an address alone never makes a desk — from this machine or from an address the node lists as a desk
    /// (<see cref="CompanionSettings.DeskAddresses"/>). A node that asks no token has no secret to prove, so there only this
    /// machine's desk is the desk.</summary>
    private bool IsDesk()
    {
        if (_strict || _helloName != DeskName)
            return false;
        var settings = _server.Settings;
        var token = Pairing.Normalise(settings.Token);
        if (token.Length == 0)
            return _loopback;
        var proved = _pairedToken is not null && string.Equals(_pairedToken, token, StringComparison.Ordinal);
        return proved && (_loopback || IsDeskAddress(_remote, settings));
    }

    private static bool IsDeskAddress(IPAddress? remote, NodeSettings settings)
    {
        if (remote is null || settings.Companion.DeskAddresses.IsDefaultOrEmpty)
            return false;
        var a = NetAddress.Normal(remote);
        foreach (var t in settings.Companion.DeskAddresses)
            if (IPAddress.TryParse(t.Trim(), out var desk) && NetAddress.Normal(desk).Equals(a))
                return true;
        return false;
    }

    /// <summary>Tells the node's client table who this is now (after HELLO, and after pairing changes what it may do).</summary>
    private void Register()
    {
        if (!_saidHello || _server.Endpoint is not IClientRegistry registry)
            return;
        registry.Hello(Info(), _channel.Kind);
        _registered = true;
    }

    public void Abort() => _channel.Abort();

    /// <summary>The door is stopping: a WebSocket hears 1001, a TCP connection is cut.</summary>
    public async Task StopAsync()
    {
        await _channel.CloseAsync(1001, "the node is stopping");
        if (!_strict)
            _channel.Abort();   // TCP has no close words: the reader wakes on the cut
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Paired)
            Writer.Say("NONCE " + _nonce);
        while (!ct.IsCancellationRequested && !Writer.Closed && !_close)
        {
            string? raw;
            try
            {
                raw = await _channel.ReadLineAsync(ct);
            }
            catch (InvalidDataException ex)
            {
                // A line that never ends is not a command: said once, and the door closed.
                Writer.Say(WireGrammar.Err(null, ex.Message + " — the wire's lines are commands, and this one was not; closed"));
                await Writer.FlushAsync(LastWordWait);
                return;
            }
            if (raw is null)
                return;
            await HandleAsync(raw, ct);
        }
        if (_close)
        {
            await Writer.FlushAsync(LastWordWait);
            await _channel.CloseAsync(_closeCode, "closed");
        }
    }

    private async Task HandleAsync(string raw, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;
        var readAt = _server.Wall.UnixMs;
        if (IsPeerReply(raw.TrimStart()))
            return;   // a peer's own answer is never a command, and answering it would be matched to its next line
        if (!WireGrammar.TryParse(raw, out var line, out var error))
        {
            Writer.Say(WireGrammar.Err(error!.RequestId, error.Reason));
            return;
        }
        if (line.Verb is "OK" or "ERR" or "ERROR")
            return;
        if (FactLines.IsFact(line))
        {
            // A fact is taken in from a paired peer and never answered, whatever the endpoint makes of it.
            if (Paired)
                await _server.Runner.HandleGuardedAsync(line, Info(), ct);
            return;
        }
        var id = line.RequestId;
        if (_strict && !Paired && !BeforePairing.Contains(line.Verb))
        {
            // The web port's line channel skips the web's token check, so until it pairs it answers only what pairing needs.
            Writer.Say(WireGrammar.Err(id, WireWords.NotPaired));
            return;
        }
        try
        {
            if (!HandleSession(line, id, readAt))
            {
                var reply = await _server.Runner.RunAsync(line, Info(), ct, readAt);
                if (CommandRunner.WireLineFor(id, reply) is { } text)
                    Writer.Say(text);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A session verb that faults (the endpoint's STATE threw under HELLO) is that line's, as any verb's is.
            Writer.Say(CommandRunner.WireLineFor(id, _server.Runner.Fault(ex, Info()))!);
        }
    }

    /// <summary>The verbs the connection answers itself; false for everything the command path runs.</summary>
    private bool HandleSession(WireLine line, string? id, long readAt)
    {
        switch (line.Verb)
        {
            case "PING":
                Ping(line, id, readAt);
                return true;
            case "NONCE":
                _nonce = Pairing.NewNonce();
                Writer.Say(WireGrammar.Ok(id, _nonce));
                return true;
            case "HELLO":
                _helloName = Pairing.HelloName(line.RestAfter(1));
                _saidHello = true;
                Writer.Say(WireGrammar.Ok(id));
                if (_strict && !Paired)
                {
                    // STATE is the node's: an unpaired line channel hears it right after "OK paired".
                    _greetOnPairing = true;
                }
                else
                {
                    Writer.SayPush(PushKind.State, "STATE " + _server.Endpoint.State().Json);
                    _wantsState = true;
                }
                Register();
                return true;
            case "AUTH-PROOF":
                Authenticate(line, id, proof: true);
                return true;
            case "AUTH":
                Authenticate(line, id, proof: false);
                return true;
            case "SUBSCRIBE":
                Subscribe(line, id);
                return true;
            default:
                return false;
        }
    }

    private static bool IsPeerReply(string t) =>
        Starts(t, "OK") || Starts(t, "ERR") || Starts(t, "ERROR");

    private static bool Starts(string t, string word) =>
        t.StartsWith(word, StringComparison.Ordinal) && (t.Length == word.Length || t[word.Length] == ' ');

    /// <summary>"OK PONG t1 t2 t3": t2 when the line was read, t3 stamped by the writer as the answer leaves.</summary>
    private void Ping(WireLine line, string? id, long readAt)
    {
        if (!CommandRunner.TryStamp(line.Word(1), out var t1))
        {
            Writer.Say(WireGrammar.Ok(id, "PONG"));
            return;
        }
        var wall = _server.Wall;
        Writer.Say(() => WireGrammar.Ok(id, CommandRunner.PongWords(t1, readAt, wall.UnixMs)));
    }

    private void Authenticate(WireLine line, string? id, bool proof)
    {
        var token = Pairing.Normalise(_server.Settings.Token);
        if (token.Length == 0)
        {
            Writer.Say(WireGrammar.Ok(id, WireWords.Open));
            Greet();
            return;
        }
        if (_server.LockedOut?.Invoke(_address) == true)
        {
            // The address spent its wrong tokens on this door: the proof is not even looked at.
            Writer.Say(WireGrammar.Err(id, WireWords.TooManyWrongTokens + " — closed"));
            _closeCode = WebSocketLineChannel.WrongProofsClose;
            _close = true;
            return;
        }
        bool right;
        if (proof)
        {
            right = Pairing.Verify(token, _nonce, _helloName, line.Word(1));
            _nonce = Pairing.NewNonce();   // a nonce answers one proof, right or wrong
        }
        else
        {
            if (_strict || !(_loopback && _server.Options.PlainAuthOnLoopback))
            {
                Writer.Say(WireGrammar.Err(id, WireWords.UseProof));
                return;
            }
            right = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Pairing.Normalise(line.RestAfter(1))));
        }
        if (right)
        {
            _pairedToken = token;
            _wrong = 0;
            Writer.Say(WireGrammar.Ok(id, WireWords.Paired));
            Greet();
            Register();
            return;
        }
        _server.WrongProof?.Invoke(_address);
        if (++_wrong >= _server.Options.Limits.WrongProofsBeforeClose)
        {
            Writer.Say(WireGrammar.Err(id, WireWords.WrongToken + " — closed"));
            _closeCode = WebSocketLineChannel.WrongProofsClose;
            _close = true;
            _server.Log.LogWarning("The line wire closed a connection from {Address} after {Count} wrong tokens.", _address, _wrong);
            return;
        }
        Writer.Say(WireGrammar.Err(id, WireWords.WrongToken));
        if (proof && !Paired)
            Writer.Say("NONCE " + _nonce);
    }

    /// <summary>The greeting STATE an unpaired line channel's HELLO waited for, right after "OK paired".</summary>
    private void Greet()
    {
        if (!_greetOnPairing)
            return;
        _greetOnPairing = false;
        Writer.SayPush(PushKind.State, "STATE " + _server.Endpoint.State().Json);
        _wantsState = true;
    }

    private void Subscribe(WireLine line, string? id)
    {
        switch (line.WordUpper(1))
        {
            case "STATE":
                Writer.Say(WireGrammar.Ok(id, "subscribed state"));
                Writer.SayPush(PushKind.State, "STATE " + _server.Endpoint.State().Json);
                _wantsState = true;
                return;
            case "CONTEXT":
                Writer.Say(WireGrammar.Ok(id, "subscribed context"));
                Writer.SayPush(PushKind.Context, "CONTEXT " + _server.Endpoint.Context());
                _wantsContext = true;
                return;
            case "PROJECT":
                Writer.Say(WireGrammar.Ok(id, "subscribed project"));
                Writer.SayPush(PushKind.Project, _server.ProjectLine());
                _wantsProject = true;
                return;
            case "OFF":
                _wantsState = false;
                _wantsContext = false;
                _wantsProject = false;
                _topics.Dispose();
                Writer.Say(WireGrammar.Ok(id, "pushes off"));
                return;
            case "":
                Writer.Say(WireGrammar.Err(id, WireWords.Subscribe));
                return;
            default:
                // A package's topic (INDEX, EVENTS, ANCHORS …), asked of the endpoint.
                Writer.Say(_topics.TryOpen(_server.Endpoint, line.Word(1), line.Words.Skip(2).ToList(), Info(), t => new WireTopicSink(Writer, t), out var words)
                    ? WireGrammar.Ok(id, "subscribed " + words.ToLowerInvariant())
                    : WireGrammar.Err(id, words));
                _topics.Release();
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _topics.Dispose();
        if (_registered && _server.Endpoint is IClientRegistry registry)
            registry.Gone(_id);
        await Writer.DisposeAsync();
    }
}
