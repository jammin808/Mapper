using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Net.Links;

/// <summary>The desk's view of an engine over the line wire — its own engine on loopback or a node across the
/// network (docs/ARCHITECTURE.md §1: the desk has no private API). It says HELLO PlayMapDesk (which makes it a STATE
/// listener), proves the node's token when the node asks, subscribes to PROJECT pushes, and mirrors STATE and the
/// project latest-wins (a reader takes the newest; nothing is queued per change). Lines carry request ids and their
/// replies are matched by them; nothing is queued while the link is down; it redials with the contract's backoff.</summary>
public sealed class EngineLinkClient : IEngineLink
{
    private readonly WireClient _wire;
    private readonly ILogger? _log;
    private readonly object _gate = new();
    private (string Json, long Revision) _state = ("", 0);
    private (string Json, long Revision) _project = ("", 0);
    private string _nodeName = "";
    private string _nodeId = "";

    /// <param name="token">The node's pairing token ("" when it asks none, or trusts this machine).</param>
    public EngineLinkClient(string host, int port, string token, ShowWallClock? wall = null, ILogger? log = null, TimeSpan? staleAfter = null, TimeSpan? dropAfter = null)
    {
        _log = log;
        _wire = new WireClient(new WireClientOptions
        {
            Host = host,
            Port = port,
            ClientName = "PlayMapDesk",
            Token = token ?? "",
            IdPrefix = "d",
            MaxLineBytes = 256 * 1024 * 1024,
            LineSeconds = TimeSpan.FromSeconds(60),
            StaleAfter = staleAfter ?? TimeSpan.FromMilliseconds(ShowBusConstants.StaleMs),
            DropAfter = dropAfter ?? TimeSpan.FromMilliseconds(ShowBusConstants.GoneMs),
            OnConnected = async ct =>
            {
                var reply = await _wire!.SendAsync("SUBSCRIBE PROJECT", ct);
                if (!reply.Ok)
                    _log?.LogWarning("The engine refused the project subscription: {Words}", reply.Text);
            },
        }, wall ?? ShowWallClock.System(), log);
        _wire.Line += OnLine;
        _wire.StatusChanged += s => StatusChanged?.Invoke(s);
    }

    public LinkStatus Status => _wire.Status;

    public PairState Pairing => _wire.Pairing;

    public string NodeName
    {
        get
        {
            lock (_gate)
                return _nodeName;
        }
    }

    public string NodeId
    {
        get
        {
            lock (_gate)
                return _nodeId;
        }
    }

    public (string Json, long Revision) State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public (string Json, long Revision) Project
    {
        get
        {
            lock (_gate)
                return _project;
        }
    }

    /// <summary>The engine's clock minus this machine's (ms), once a PING exchange closed.</summary>
    public (double OffsetMs, double RoundTripMs, int Samples) Clock => _wire.Clock;

    public event Action<LinkStatus>? StatusChanged;

    public event Action<long>? StateChanged;

    public event Action<long>? ProjectChanged;

    public void Start() => _wire.Start();

    public Task<WireReply> SendAsync(string line, CancellationToken ct = default) => _wire.SendAsync(line, ct);

    private void OnLine(string line)
    {
        if (line.StartsWith("STATE ", StringComparison.Ordinal))
            TakeState(line[6..]);
        else if (line.StartsWith("PROJECT ", StringComparison.Ordinal))
            TakeProject(line[8..]);
    }

    private void TakeState(string json)
    {
        using var doc = JsonText.TryParse(json, int.MaxValue);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return;
        var r = doc.RootElement;
        var seq = JsonText.Long(r, "seq");
        lock (_gate)
        {
            _state = (json, seq);
            if (JsonText.Str(r, "id") is { Length: > 0 } id)
                _nodeId = id;
            if (JsonText.Str(r, "name") is { Length: > 0 } name)
                _nodeName = name;
        }
        StateChanged?.Invoke(seq);
    }

    /// <summary>{"revision":N,"project":{…}} — the revision rides beside the document.</summary>
    private void TakeProject(string envelope)
    {
        using var doc = JsonText.TryParse(envelope, int.MaxValue);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("project", out var project))
            return;
        var revision = JsonText.Long(doc.RootElement, "revision");
        lock (_gate)
            _project = (project.GetRawText(), revision);
        ProjectChanged?.Invoke(revision);
    }

    public ValueTask DisposeAsync() => _wire.DisposeAsync();
}
