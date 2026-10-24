using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Assistant;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Assistant;

/// <summary>Where the approval desk is and how this node speaks to it.</summary>
public sealed record StagewatchSinkOptions
{
    /// <summary>The Stagewatch's HTTP(S) root, e.g. http://10.0.0.6:9924/.</summary>
    public required Uri BaseUri { get; init; }

    /// <summary>A Stagewatch node key (swk_…). It rides in the Authorization header only — never in a URL, a line or
    /// the journal. Stagewatch binds it to the first node id it speaks for, so it can only collect this node's
    /// approvals.</summary>
    public required string Key { get; init; }

    /// <summary>This node's Show Bus id and name (the proposal's "from" and "target").</summary>
    public required string SelfId { get; init; }

    public string SelfName { get; init; } = "PlayMap";

    /// <summary>How often decisions are pulled (Stagewatch marks an approved proposal undelivered after 60 s).</summary>
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Collect what was approved for this node and run it. Off when another collector on this node (the
    /// Stagewatch link's own approvals pull) does it: the sink then only reads how its proposals ended.</summary>
    public bool CollectApproved { get; init; } = true;

    /// <summary>The handler requests go through (TLS pinning from the host; tests). Null: a plain handler.</summary>
    public HttpMessageHandler? Handler { get; init; }
}

/// <summary>
/// The node's proposals sent to a Stagewatch approval desk (Show Bus 1.2 §11): each is posted to
/// <c>POST /api/proposals</c> as {from, target (this node), line, reason, risk} and waits there for the show caller;
/// the line goes as it is shown (its secret words replaced by a marker), and once approved the node runs its own record
/// of it, never the desk's copy.
/// Stagewatch pushes nothing back, so decisions are pulled: what was approved for this node is collected with
/// <c>GET /api/proposals?target=&lt;id&gt;&amp;state=approved</c>, run once through the node's own command path (its
/// fences still apply) and answered with <c>POST /api/proposals/&lt;id&gt;/result {"ok","words"}</c>; and
/// <c>GET /api/proposals?from=&lt;id&gt;</c> tells which of this node's proposals were rejected, done, failed or
/// undelivered. Each decision completes the node's own record of the proposal
/// (<see cref="ProposalQueue.CompleteForwarded"/>), so the assistant and STATE say what the desk decided and what
/// ran. The key is a header, never a URL.
/// </summary>
public sealed class StagewatchProposalSink : PlayMap.Assistant.IProposalSink, IAsyncDisposable
{
    private readonly StagewatchSinkOptions _options;
    private readonly ProposalQueue _queue;
    private readonly IWireEndpoint _endpoint;
    private readonly Action<string> _log;
    private readonly ILogger _engineLog;
    private readonly HttpClient _http;
    private readonly HashSet<string> _ran = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _disposed;

    /// <param name="log">The journal's words.</param>
    /// <param name="engineLog">Where a fault nothing in the sink expects is written with its trace (the engine's log).</param>
    public StagewatchProposalSink(StagewatchSinkOptions options, ProposalQueue queue, IWireEndpoint endpoint, Action<string>? log = null, ILogger? engineLog = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(endpoint);
        _options = options;
        _queue = queue;
        _endpoint = endpoint;
        _log = log ?? Ignore;
        _engineLog = engineLog ?? NullLogger.Instance;
        _http = options.Handler is { } h ? new HttpClient(h, disposeHandler: false) : new HttpClient();
        _http.BaseAddress = options.BaseUri;
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public string Name => "Stagewatch at " + _options.BaseUri.Authority;

    /// <summary>Lines run from the desk's approvals (this node's own and other proposers').</summary>
    public long Ran { get; private set; }

    /// <summary>The last failure talking to the desk, in words ("" while it answers).</summary>
    public string Problem { get; private set; } = "";

    /// <summary>Starts pulling decisions every <see cref="StagewatchSinkOptions.PollEvery"/>.</summary>
    public void Start()
    {
        lock (_gate)
            _loop ??= Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
    }

    public async Task<string?> OfferAsync(Proposal proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var body = JsonOut(w =>
        {
            w.WriteStartObject();
            Node(w, "from");
            Node(w, "target");
            // The desk shows the line without its secret words; the node keeps them and runs its own record.
            w.WriteString("line", proposal.Shown);
            w.WriteString("reason", proposal.Reason);
            w.WriteString("risk", proposal.RiskWord);
            w.WriteEndObject();
        });
        using var request = Request(HttpMethod.Post, "api/proposals", body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Problem = $"{Name} did not take {proposal.Id}: {(int)response.StatusCode} {ErrorOf(text)}";
            _log("assistant: " + Problem);
            return null;
        }
        using var doc = JsonDocument.Parse(text);
        var id = Str(doc.RootElement, "id");
        Problem = "";
        return id.Length is > 0 and <= 64 ? id : null;
    }

    /// <summary>One pull: the approved lines for this node run and answered, then this node's proposals' ends read
    /// back. A desk that does not answer is tried again on the next pull; nothing runs twice.</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            if (_options.CollectApproved)
                await CollectAsync(ct).ConfigureAwait(false);
            await ReadOwnAsync(ct).ConfigureAwait(false);
            Problem = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Problem = $"{Name} could not be read: {ex.Message}";
        }
    }

    private async Task CollectAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "api/proposals?target=" + Uri.EscapeDataString(_options.SelfId) + "&state=approved");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Problem = $"{Name} would not hand over this node's approvals: {(int)response.StatusCode}";
            return;
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        foreach (var item in Items(doc.RootElement))
        {
            var id = Str(item, "id");
            var target = item.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Object ? Str(t, "id") : "";
            if (id.Length is 0 or > 64 || (target.Length > 0 && target != _options.SelfId) || Str(item, "state") is { Length: > 0 } s && s != "approved")
                continue;
            lock (_gate)
            {
                if (!_ran.Add(id))
                    continue;
            }
            var decidedBy = Str(item, "decidedBy");
            // This node's own proposal runs from its own record (the desk only ever held the shown copy).
            var line = OwnForwarded(id) is { } own ? own.Line : Str(item, "line");
            var (ok, words) = await RunAsync(id, line, decidedBy, ct).ConfigureAwait(false);
            var result = JsonOut(w =>
            {
                w.WriteStartObject();
                w.WriteBoolean("ok", ok);
                w.WriteString("words", words.Length > 256 ? words[..256] : words);
                w.WriteEndObject();
            });
            // The desk records the result; its answer adds nothing the node needs.
            using var answer = Request(HttpMethod.Post, "api/proposals/" + Uri.EscapeDataString(id) + "/result", result);
            using var _ = await _http.SendAsync(answer, ct).ConfigureAwait(false);
            Complete(id, ok, words, decidedBy);
        }
    }

    /// <summary>The approved line through the node's own command path, as the desk's approval.</summary>
    private async Task<(bool Ok, string Words)> RunAsync(string id, string text, string decidedBy, CancellationToken ct)
    {
        if (!WireGrammar.TryParse(text, out var line, out var error))
            return (false, error?.Reason ?? "the line could not be read");
        if (line.Verb is "HELLO" or "NONCE" or "AUTH" or "AUTH-PROOF" or "SUBSCRIBE")
            return (false, line.Verb + " is the connection's own word — the transport answers it");
        var who = decidedBy.Length > 0 ? $"{decidedBy} on {Name}" : Name;
        var session = new SessionInfo("approval:" + id, "approval", _options.BaseUri.Host, false, who, Paired: true);
        var reply = await _endpoint.HandleAsync(line with { RequestId = null }, session, ct).ConfigureAwait(false);
        Ran++;
        return (reply.Ok, ProposalQueue.ReplyWords(reply));
    }

    private async Task ReadOwnAsync(CancellationToken ct)
    {
        var waiting = _queue.All.Where(p => p.State == ProposalState.Forwarded && p.RemoteDesk == Name).ToList();
        if (waiting.Count == 0)
            return;
        using var request = Request(HttpMethod.Get, "api/proposals?from=" + Uri.EscapeDataString(_options.SelfId));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        foreach (var item in Items(doc.RootElement))
        {
            var id = Str(item, "id");
            var state = Str(item, "state");
            var words = Str(item, "words") is { Length: > 0 } w ? w : Str(item, "result");
            var decidedBy = Str(item, "decidedBy");
            switch (state)
            {
                case "rejected":
                    Complete(id, null, words, decidedBy);
                    break;
                case "done":
                    Complete(id, true, words, decidedBy);
                    break;
                case "failed" or "undelivered":
                    Complete(id, false, state == "undelivered" && words.Length == 0 ? "undelivered: the node did not collect it in time" : words, decidedBy);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>This node's proposal the desk holds under its id, while it waits there; null for another proposer's.</summary>
    private Proposal? OwnForwarded(string remoteId) =>
        _queue.All.FirstOrDefault(p => p.State == ProposalState.Forwarded && p.RemoteDesk == Name && p.RemoteId == remoteId);

    /// <summary>The desk's decision for one of this node's forwarded proposals; a proposal another node made, or one
    /// already completed, is left alone.</summary>
    private void Complete(string remoteId, bool? ok, string words, string decidedBy)
    {
        var local = OwnForwarded(remoteId);
        if (local is null)
            return;
        try
        {
            _queue.CompleteForwarded(local.Id, ok, words, decidedBy.Length > 0 ? $"{decidedBy} on {Name}" : Name);
        }
        catch (InvalidOperationException ex)
        {
            // Decided by another path in the meantime (the node's own list, an expiry): the first decision stands.
            _log("assistant: " + ex.Message);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
                await Task.Delay(_options.PollEvery, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Once the stop is taken, whatever the call in flight ends on is the stop's (a refusal, a reset or a body cut
                // short as well as the cancel): the pull ends and names nothing.
                return;
            }
            catch (Exception ex)
            {
                // A failure no pull expects ends the pull: said at once on the sink, written with its trace to the engine's log and
                // said in the journal, never thrown at whoever lets the desk go.
                Problem = $"{Name} is no longer read: {ex.Message}";
                _engineLog.LogWarning(ex, "The approval desk's pull from {Desk} faulted and ended.", Name);
                _log("assistant: " + Problem);
                return;
            }
        }
    }

    private static void Ignore(string line) => _ = line;

    private HttpRequestMessage Request(HttpMethod method, string path, string? json = null)
    {
        var r = new HttpRequestMessage(method, path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Key);
        if (json is not null)
            r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return r;
    }

    private void Node(Utf8JsonWriter w, string name)
    {
        w.WriteStartObject(name);
        w.WriteString("t", Core.Bus.ShowBusConstants.KindPlayMap);
        w.WriteString("id", _options.SelfId);
        w.WriteString("name", _options.SelfName);
        w.WriteEndObject();
    }

    /// <summary>A list reply: a bare array or an object with "proposals" (Stagewatch answers the latter).</summary>
    private static List<JsonElement> Items(JsonElement root)
    {
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("proposals", out var p) && p.ValueKind == JsonValueKind.Array ? p
            : default;
        return array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Take(500).ToList() : [];
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string ErrorOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Str(doc.RootElement, "error");
        }
        catch (JsonException)
        {
            return body.Length > 120 ? body[..120] : body;
        }
    }

    private static string JsonOut(Action<Utf8JsonWriter> write) => EngineCore.JsonOut(write);

    /// <summary>Lets the desk go, once: the stop is taken, the pull ends on it, and the sink's client and stop go once it has.
    /// Nothing reaches whoever lets the desk go: the pull ends without throwing, and a callback on its stop that throws is written
    /// with its trace to the engine's log.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            try
            {
                await _stop.CancelAsync().ConfigureAwait(false);
            }
            catch (AggregateException ex)
            {
                // A callback on the stop that throws is its registrant's fault, not the desk's: every callback still runs and the
                // stop stands, so the pull is waited for as on any stop.
                _engineLog.LogWarning(ex, "A callback on the stop of the approval desk's pull from {Desk} faulted.", Name);
            }
            Task? loop;
            lock (_gate)
                loop = _loop;
            if (loop is not null)
                await loop.ConfigureAwait(false);
        }
        finally
        {
            _http.Dispose();
            _stop.Dispose();
        }
    }

    public override string ToString() => Name;
}
