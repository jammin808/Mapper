using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net.Links;

namespace PlayMap.Net.Fleet;

/// <summary>A backup's side of its primary's mirror: a line-wire link that proves the primary's token against its NONCE (the token
/// never crosses the network), then says <c>SUBSCRIBE MIRROR &lt;node&gt;</c> on every connection and hands each <c>MIRROR …</c> line on.
/// It redials with the link's backoff while the primary is away; a refused subscription is said once per connection
/// (<see cref="Refused"/>). It moves lines; the engine runs them through its own verb.</summary>
public sealed class MirrorLink : IAsyncDisposable
{
    private readonly WireClient _link;
    private readonly ILogger _log;
    private volatile string _refused = "";

    public MirrorLink(string host, int port, string clientName, string token, string selfNode, ShowWallClock wall, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(selfNode);
        _log = log ?? NullLogger.Instance;
        WireClient? link = null;
        link = new WireClient(new WireClientOptions
        {
            Host = host,
            Port = port,
            ClientName = clientName,
            Token = token ?? "",
            IdPrefix = "m",
            OnConnected = async ct =>
            {
                // A mutating subscription waits for the pairing to settle: the proof answers the NONCE beside this greeting.
                var waited = 0;
                while (link!.Pairing == PairState.Unknown && waited < 40 && await Waits.DelayAsync(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false))
                    waited++;
                Subscribing?.Invoke();
                var reply = await link.SendAsync("SUBSCRIBE MIRROR " + selfNode, ct).ConfigureAwait(false);
                if (reply.Ok)
                {
                    _refused = "";
                    Subscribed?.Invoke();
                    return;
                }
                var words = link.Pairing switch
                {
                    PairState.NeedsToken => "it asks for its pairing token and this node holds none for it",
                    PairState.Refused => "it refused this node's proof of its pairing token",
                    _ => reply.Text,
                };
                _refused = words;
                _log.LogWarning("The primary at {Address} refused the mirror: {Why}", Address, words);
                Refused?.Invoke(words);
            },
        }, wall, _log);
        _link = link;
        _link.Line += OnLine;
        _link.StatusChanged += s => StatusChanged?.Invoke(s);
    }

    /// <summary>A <c>MIRROR …</c> line from the primary, raised on the link's reader (a handler keeps it short).</summary>
    public event Action<string>? Line;

    public event Action<LinkStatus>? StatusChanged;

    /// <summary>Raised on each connection just before it subscribes.</summary>
    public event Action? Subscribing;

    /// <summary>Raised when the primary took the subscription.</summary>
    public event Action? Subscribed;

    /// <summary>Raised with the words when the primary refused the subscription on a connection.</summary>
    public event Action<string>? Refused;

    public LinkStatus Status => _link.Status;

    public PairState Pairing => _link.Pairing;

    /// <summary>Why the last subscription was refused ("" when it was taken).</summary>
    public string RefusedWords => _refused;

    public string Address => _link.Address;

    public void Start() => _link.Start();

    /// <summary>Cuts the connection now; the link dials again and its new subscription hands the latest of every key.</summary>
    public void Drop() => _link.Drop();

    private void OnLine(string line)
    {
        // The primary closed this subscription (it fell too far behind): dial again for the latest of every key.
        if (line.StartsWith("MIRROR overflow", StringComparison.Ordinal))
            _link.Drop();
        else if (line.StartsWith(MirrorFeed.Word + " ", StringComparison.Ordinal))
            Line?.Invoke(line);
    }

    public async ValueTask DisposeAsync()
    {
        _link.Line -= OnLine;
        await _link.DisposeAsync().ConfigureAwait(false);
    }
}
