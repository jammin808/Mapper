using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net.Links;

namespace PlayMap.Net.Sync;

/// <summary>A follower's side of the group feed: a line-wire link to its leader that says HELLO and
/// <c>SUBSCRIBE ANCHORS &lt;node&gt; [&lt;output&gt;…]</c> on every connection, hands each <c>ANCHOR</c> line on (group, key, JSON), and
/// carries this node's <c>SYNC RECEIPT</c> lines back. It redials with the link's backoff while the leader is away. It moves
/// lines; the engine adopts what they say through its own verbs.</summary>
public sealed class AnchorFollower : IAsyncDisposable
{
    private readonly WireClient _link;
    private readonly ILogger _log;

    /// <param name="subscribe">The words after SUBSCRIBE ANCHORS: this node's id and its output ids.</param>
    public AnchorFollower(string host, int port, string clientName, string subscribe, ShowWallClock wall, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(subscribe);
        _log = log ?? NullLogger.Instance;
        _link = new WireClient(new WireClientOptions
        {
            Host = host,
            Port = port,
            ClientName = clientName,
            IdPrefix = "s",
            OnConnected = async ct =>
            {
                var reply = await _link!.SendAsync(("SUBSCRIBE ANCHORS " + subscribe).TrimEnd(), ct).ConfigureAwait(false);
                if (!reply.Ok)
                    _log.LogWarning("The leader at {Address} refused the anchor feed: {Why}", Address, reply.Text);
            },
        }, wall, _log);
        _link.Line += OnLine;
        _link.StatusChanged += s => StatusChanged?.Invoke(s);
    }

    /// <summary>An ANCHOR line from the leader: its group, its key (a playlist id, or <see cref="AnchorFeed.Air"/>) and its JSON.
    /// Raised on the link's reader: a handler keeps it short.</summary>
    public event Action<string, string, string>? Anchor;

    public event Action<LinkStatus>? StatusChanged;

    public LinkStatus Status => _link.Status;

    public string Address => _link.Address;

    /// <summary>ANCHOR lines taken.</summary>
    public long Taken { get; private set; }

    /// <summary>Times the leader closed the feed for overflow (each one dialled again).</summary>
    public long Overflows { get; private set; }

    public void Start() => _link.Start();

    /// <summary>Cuts the connection now; the link dials again and its new subscription hands the latest of every key.</summary>
    public void Drop() => _link.Drop();

    /// <summary>This node's receipt for an air line; false when the link is down (the leader reads the member as silent).</summary>
    public async Task<bool> ReceiptAsync(long seq, string stage, long appliedLeaderNs, string words, CancellationToken ct)
    {
        var said = words.Replace('\n', ' ').Replace('\r', ' ').Trim();
        var line = string.Create(CultureInfo.InvariantCulture, $"SYNC RECEIPT {seq} {stage.ToUpperInvariant()} {appliedLeaderNs}") + (said.Length > 0 ? " " + said : "");
        var reply = await _link.SendAsync(line, ct).ConfigureAwait(false);
        return reply.Ok;
    }

    private void OnLine(string line)
    {
        // The leader closed this subscription (it fell too far behind): dial again, and the new subscription hands the latest of
        // every key.
        if (line.StartsWith("ANCHORS overflow", StringComparison.Ordinal))
        {
            _log.LogWarning("The leader at {Address} closed the anchor feed (overflow); dialling again.", Address);
            Overflows++;
            _link.Drop();
            return;
        }
        if (!AnchorFeed.TryParse(line, out var group, out var key, out var json))
            return;
        Taken++;
        Anchor?.Invoke(group, key, json);
    }

    public async ValueTask DisposeAsync()
    {
        _link.Line -= OnLine;
        await _link.DisposeAsync().ConfigureAwait(false);
    }
}
