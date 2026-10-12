using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Wire;
using PlayMap.Net.Commands;

namespace PlayMap.Net.Wire;

/// <summary>One connection's life on the line wire (docs/WIRE.md "A connection's life"). Lines are read and run
/// one at a time, so replies leave in the order their lines came; the session's writer is its only voice.
/// Pairing is the connection's: a proof answers this connection's nonce, a nonce answers one proof, and the token
/// a proof matched is compared with the node's token on every verb (a new token unpairs at once).</summary>
internal sealed class WireSession : IAsyncDisposable
{
    private static readonly TimeSpan LastWordWait = TimeSpan.FromSeconds(2);

    private readonly WireServer _server;
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly IPAddress? _remote;
    private readonly string _address;
    private readonly bool _loopback;
    private readonly string _id;
    private readonly LineReader _reader;
    private string _nonce = Pairing.NewNonce();
    private string _helloName = "";
    private bool _saidHello;
    private string? _pairedToken;
    private int _wrong;
    private bool _close;
    private volatile bool _wantsState;
    private volatile bool _wantsContext;
    private volatile bool _wantsProject;

    public WireSession(WireServer server, Socket socket, NetworkStream stream, IPAddress? remote, string id)
    {
        _server = server;
        _socket = socket;
        _stream = stream;
        _remote = remote;
        _address = NetAddress.Text(remote);
        _loopback = NetAddress.IsLoopback(remote);
        _id = id;
        var limits = server.Options.Limits;
        _reader = new LineReader(stream, limits.MaxLineBytes, TimeSpan.FromSeconds(limits.LineSeconds));
        Writer = new LineWriter(stream, limits.MaxQueuedReplies, TimeSpan.FromSeconds(limits.WriteSeconds), Abort);
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

    /// <summary>Who this is for the command router and the journal: the desk says "PlayMapDesk", Stagewatch's VT link
    /// says "Stagewatch", an address on the trusted list that never said hello is a Patterns "Lines" device.</summary>
    private SessionInfo Info()
    {
        var transport = _helloName switch
        {
            "PlayMapDesk" => "desk",
            "Stagewatch" => "stagewatch",
            _ when !_saidHello && NetAddress.IsListed(_remote, _server.Settings) => "patterns-device",
            _ => "wire",
        };
        return new SessionInfo(_id, transport, _address, _loopback, _helloName, Paired);
    }

    public void Abort()
    {
        try
        {
            _socket.Close(0);
        }
        catch (ObjectDisposedException)
        {
            // Closed already.
        }
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
                raw = await _reader.ReadLineAsync(ct);
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
            await Writer.FlushAsync(LastWordWait);
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
                Writer.SayPush(PushKind.State, "STATE " + _server.Endpoint.State().Json);
                _wantsState = true;
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
            if (!(_loopback && _server.Options.PlainAuthOnLoopback))
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
            return;
        }
        if (++_wrong >= _server.Options.Limits.WrongProofsBeforeClose)
        {
            Writer.Say(WireGrammar.Err(id, WireWords.WrongToken + " — closed"));
            _close = true;
            _server.Log.LogWarning("The line wire closed a connection from {Address} after {Count} wrong tokens.", _address, _wrong);
            return;
        }
        Writer.Say(WireGrammar.Err(id, WireWords.WrongToken));
        if (proof && !Paired)
            Writer.Say("NONCE " + _nonce);
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
                Writer.Say(WireGrammar.Ok(id, "pushes off"));
                return;
            default:
                Writer.Say(WireGrammar.Err(id, WireWords.Subscribe));
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Writer.DisposeAsync();
        await _stream.DisposeAsync();
    }
}
