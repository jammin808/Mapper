using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PlayMap.Assistant;
using PlayMap.Engine.Assistant;
using PlayMap.Tests;

namespace PlayMap.Engine.Tests;

/// <summary>A Stagewatch approval desk as Show Bus §11 has it (Stagewatch's own ProposalDesk, played small): POST
/// /api/proposals takes a proposal and answers its id; GET ?target=&lt;id&gt;&amp;state=approved hands over what was
/// approved for a node (once); POST /api/proposals/&lt;id&gt;/result records what it did; GET ?from=&lt;id&gt; lists a
/// proposer's own with their states. Every request's path, query and Authorization are kept. Its answers to proposals can be held
/// (<see cref="HoldAnswers"/>): a desk that records a proposal at once and answers late.</summary>
internal sealed class FakeApprovalDesk : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, JsonObject> _proposals = new(StringComparer.Ordinal);
    private Task? _answer;
    private int _next;

    private FakeApprovalDesk(WebApplication app) => _app = app;

    public int Port { get; private set; }

    public ConcurrentQueue<string> Asked { get; } = new();

    public ConcurrentQueue<string> Authorizations { get; } = new();

    public ConcurrentQueue<string> Bodies { get; } = new();

    public ConcurrentQueue<(string Id, string Body)> Answers { get; } = new();

    /// <summary>The proposals recorded whose answer was held, in the order they came.</summary>
    public ConcurrentQueue<string> Held { get; } = new();

    /// <summary>From now on each proposal posted is recorded at once (the desk holds it) and its answer waits for
    /// <paramref name="answer"/>, or ends unsent if the node gives up first.</summary>
    public void HoldAnswers(Task answer)
    {
        lock (_gate)
            _answer = answer;
    }

    public static async Task<FakeApprovalDesk> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        var app = builder.Build();
        var desk = new FakeApprovalDesk(app);
        app.MapPost("/api/proposals", async (HttpContext c) =>
        {
            desk.Record(c);
            using var r = new StreamReader(c.Request.Body);
            var body = await r.ReadToEndAsync();
            desk.Bodies.Enqueue(body);
            var p = JsonNode.Parse(body)!.AsObject();
            string id;
            Task? answer;
            lock (desk._gate)
            {
                id = "p" + (++desk._next).ToString(System.Globalization.CultureInfo.InvariantCulture);
                p["id"] = id;
                p["state"] = "pending";
                desk._proposals[id] = p;
                answer = desk._answer;
            }
            if (answer is not null)
            {
                desk.Held.Enqueue(id);
                await Task.WhenAny(answer, Task.Delay(Timeout.Infinite, c.RequestAborted));
            }
            return Microsoft.AspNetCore.Http.Results.Json(new { ok = true, id, state = "pending", risk = p["risk"]?.GetValue<string>() ?? "high" }, statusCode: 201);
        });
        app.MapGet("/api/proposals", (HttpContext c) =>
        {
            desk.Record(c);
            var target = c.Request.Query["target"].ToString();
            var from = c.Request.Query["from"].ToString();
            var list = new JsonArray();
            lock (desk._gate)
            {
                foreach (var p in desk._proposals.Values)
                {
                    if (target.Length > 0 && p["state"]!.GetValue<string>() == "approved" && p["claimed"] is null && p["target"]!["id"]!.GetValue<string>() == target)
                    {
                        p["claimed"] = true;
                        list.Add(p.DeepClone());
                    }
                    else if (from.Length > 0 && p["from"]!["id"]!.GetValue<string>() == from)
                    {
                        list.Add(p.DeepClone());
                    }
                }
            }
            return Microsoft.AspNetCore.Http.Results.Text(new JsonObject { ["proposals"] = list }.ToJsonString(), "application/json");
        });
        app.MapPost("/api/proposals/{id}/result", async (HttpContext c) =>
        {
            desk.Record(c);
            using var r = new StreamReader(c.Request.Body);
            var body = await r.ReadToEndAsync();
            var id = (string)c.Request.RouteValues["id"]!;
            desk.Answers.Enqueue((id, body));
            var result = JsonNode.Parse(body)!.AsObject();
            lock (desk._gate)
            {
                var p = desk._proposals[id];
                p["state"] = result["ok"]!.GetValue<bool>() ? "done" : "failed";
                p["words"] = result["words"]!.GetValue<string>();
            }
            return Microsoft.AspNetCore.Http.Results.Json(new { ok = true });
        });
        await app.StartAsync();
        desk.Port = new Uri(app.Urls.First()).Port;
        return desk;
    }

    /// <summary>The show caller decides on the desk.</summary>
    public void Decide(string id, string state, string by)
    {
        lock (_gate)
        {
            _proposals[id]["state"] = state;
            _proposals[id]["decidedBy"] = by;
        }
    }

    /// <summary>A proposal another app made for this node, already approved.</summary>
    public string AddApproved(string targetId, string line)
    {
        lock (_gate)
        {
            var id = "p" + (++_next).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _proposals[id] = new JsonObject
            {
                ["id"] = id,
                ["from"] = new JsonObject { ["t"] = "patterns", ["id"] = "9f8e7d6c" },
                ["target"] = new JsonObject { ["t"] = "playmap", ["id"] = targetId },
                ["line"] = line,
                ["state"] = "approved",
                ["decidedBy"] = "Caller",
            };
            return id;
        }
    }

    private void Record(HttpContext c)
    {
        Asked.Enqueue(c.Request.Method + " " + c.Request.Path + c.Request.QueryString.Value);
        Authorizations.Enqueue(c.Request.Headers.Authorization.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>A node's network control that keeps the token its first <c>SYNC LEAD</c> ran with: for a line the approval desk
/// handed over, the token of the pull that collected it, which is the sink's own stop.</summary>
internal sealed class PullTokenSpy : INetworkControl
{
    private readonly TaskCompletionSource<CancellationToken> _token = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<CancellationToken> Token => _token.Task;

    public Task<string> LeadAsync(string group, CancellationToken ct)
    {
        _token.TrySetResult(ct);
        return Task.FromResult("leading " + group);
    }

    public Task<string> LinkPatternsAsync(string? host, string? token, CancellationToken ct) => Task.FromResult("following Patterns at " + host);

    public Task<string> LinkStagewatchAsync(string? host, string? key, CancellationToken ct) => Task.FromResult("following Stagewatch at " + host);

    public Task<string> FollowAsync(string group, string nodeId, CancellationToken ct) => Task.FromResult("following " + nodeId);

    public Task<string> FetchAsync(IReadOnlyList<PlayMap.Core.Model.MediaItem> items, string? fromNode, CancellationToken ct) => Task.FromResult("nothing to fetch");
}

/// <summary>An approval desk played request by request, whose refusal lands as the pull's stop is taken. Its first collect
/// hands over a line another app proposed for this node (the node runs it with the pull's stop as its token, which the
/// <see cref="PullTokenSpy"/> keeps); its second is refused at once; its third is held and refused once the stop is taken, in
/// the stop's own callback, before the stop has reached the request's token: the order in which a connect to a closed port
/// that is refused late (on Windows about 2 s on loopback) can meet a stop taken with <c>CancelAsync</c>, whose callbacks run
/// after it returns. Results are taken with 200.</summary>
internal sealed class LateRefusalDesk(PullTokenSpy spy) : HttpMessageHandler
{
    public const string EarlyWords = "the desk is not up yet";

    public const string LateWords = "No connection could be made because the target machine actively refused it.";

    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _collects;

    /// <summary>Completes once the third collect is held, its refusal waiting on the pull's stop.</summary>
    public Task Held => _held.Task;

    /// <summary>The pull's stop was taken when the late refusal was raised.</summary>
    public bool RaisedAfterTheStop { get; private set; }

    /// <summary>The request's own token was not cancelled yet when the late refusal was raised, so the client hands the
    /// refusal on as it is rather than as a cancel.</summary>
    public bool RaisedBeforeTheRequestsCancel { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post)
            return Task.FromResult(Reply(request, "{\"ok\":true}"));
        return Interlocked.Increment(ref _collects) switch
        {
            1 => Task.FromResult(Reply(request, "{\"proposals\":[{\"id\":\"p9\",\"line\":\"SYNC LEAD g1\",\"state\":\"approved\",\"decidedBy\":\"Caller\"}]}")),
            2 => Task.FromException<HttpResponseMessage>(Refused(EarlyWords)),
            _ => HeldAsync(cancellationToken),
        };
    }

    private async Task<HttpResponseMessage> HeldAsync(CancellationToken request)
    {
        var stop = await spy.Token.WaitAsync(request).ConfigureAwait(false);
        // Its continuations run where it is completed: the client and the pull see the refusal inside the stop's callback.
        var refusal = new TaskCompletionSource<HttpResponseMessage>();
        // Registered after the client linked the request's token to the stop, so it runs first when the stop is taken (a
        // token's callbacks run newest first), before the stop reaches the request.
        using var onStop = stop.Register(() =>
        {
            RaisedAfterTheStop = stop.IsCancellationRequested;
            RaisedBeforeTheRequestsCancel = !request.IsCancellationRequested;
            refusal.TrySetException(Refused(LateWords));
        });
        using var onRequest = request.Register(() => refusal.TrySetCanceled(request));
        _held.TrySetResult();
        return await refusal.Task.ConfigureAwait(false);
    }

    private static HttpResponseMessage Reply(HttpRequestMessage request, string json) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"), RequestMessage = request };

    private static HttpRequestException Refused(string words) =>
        new(HttpRequestError.ConnectionError, words, new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
}

/// <summary>An approval desk with nothing waiting: every pull is answered with an empty list.</summary>
internal sealed class QuietDesk : HttpMessageHandler
{
    private int _asked;

    public int Asked => Volatile.Read(ref _asked);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _asked);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"proposals\":[]}", System.Text.Encoding.UTF8, "application/json"),
            RequestMessage = request,
        });
    }
}

/// <summary>An approval desk that never answers: every request waits until its own token is cancelled.</summary>
internal sealed class SilentDesk : HttpMessageHandler
{
    private int _asked;

    public int Asked => Volatile.Read(ref _asked);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _asked);
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("an endless wait ended without its cancel");
    }
}

/// <summary>An approval desk that answers every offer (POST /api/proposals) with one status and body, and every other request with
/// an empty list.</summary>
internal sealed class AnsweringDesk(System.Net.HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var offer = request.Method == HttpMethod.Post;
        return Task.FromResult(new HttpResponseMessage(offer ? status : System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(offer ? body : "{\"proposals\":[]}", System.Text.Encoding.UTF8, "application/json"),
            RequestMessage = request,
        });
    }
}

/// <summary>An approval desk that never answers an offer: each POST to /api/proposals waits for its request's cancel and then for
/// the test to open the gate before it ends as that cancel, so a cancel can reach the request well before the request ends. Every
/// other request is answered with an empty list.</summary>
internal sealed class HeldOfferDesk : HttpMessageHandler
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _held;
    private int _cancelled;

    /// <summary>Offers that reached the desk, each waiting for its cancel.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>Offers whose cancel has reached the desk.</summary>
    public int Cancelled => Volatile.Read(ref _cancelled);

    /// <summary>Lets every offer end once its cancel has reached it.</summary>
    public void Open() => _gate.TrySetResult();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post)
            return HeldAsync(cancellationToken);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"proposals\":[]}", System.Text.Encoding.UTF8, "application/json"),
            RequestMessage = request,
        });
    }

    private async Task<HttpResponseMessage> HeldAsync(CancellationToken request)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var onCancel = request.Register(() =>
        {
            Interlocked.Increment(ref _cancelled);
            cancelled.TrySetResult();
        });
        // Counted once its cancel is watched, so an offer the test sees held is one a cancel is seen reaching.
        Interlocked.Increment(ref _held);
        await Task.WhenAll(cancelled.Task, _gate.Task).ConfigureAwait(false);
        throw new OperationCanceledException(request);
    }
}

/// <summary>An approval desk whose client fails as no pull expects, at every request.</summary>
internal sealed class BrokenClientDesk : HttpMessageHandler
{
    public const string Words = "the desk's client is in no state to send";

    private int _asked;

    public int Asked => Volatile.Read(ref _asked);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _asked);
        throw new InvalidOperationException(Words);
    }
}

/// <summary>An approval desk whose first collect is held past the pull's stop. A callback on the request's token throws when the stop
/// reaches it (a fault of whoever registered it, which the stop's cancel carries once every callback has run), and the request ends
/// only once the test opens its gate. Every other request is answered with an empty list.</summary>
internal sealed class ThrowingStopDesk : HttpMessageHandler
{
    public const string Words = "a callback on the pull's stop is in no state to run";

    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _collects;
    private int _ended;

    /// <summary>Completes once the first collect is held, its callbacks on the request's token registered.</summary>
    public Task Held => _held.Task;

    /// <summary>The held collect has ended (as the stop's cancel).</summary>
    public bool Ended => Volatile.Read(ref _ended) == 1;

    /// <summary>Lets the held collect end once its request is cancelled.</summary>
    public void Open() => _gate.TrySetResult();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get && Interlocked.Increment(ref _collects) == 1)
            return HeldAsync(cancellationToken);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"proposals\":[]}", System.Text.Encoding.UTF8, "application/json"),
            RequestMessage = request,
        });
    }

    private async Task<HttpResponseMessage> HeldAsync(CancellationToken request)
    {
        try
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var onCancel = request.Register(() => cancelled.TrySetResult());
            // Registered last, so it runs first when the stop reaches the request (a token's callbacks run newest first); the
            // others still run.
            using var faulty = request.Register(() => throw new InvalidOperationException(Words));
            _held.TrySetResult();
            await Task.WhenAll(cancelled.Task, _gate.Task).ConfigureAwait(false);
            throw new OperationCanceledException(request);
        }
        finally
        {
            Volatile.Write(ref _ended, 1);
        }
    }
}

/// <summary>An engine log that keeps what it is given, the fault with its words.</summary>
internal sealed class KeptFaults : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Words, Exception? Fault)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception), exception));
}

/// <summary>The node's proposals on a Stagewatch approval desk: offered with the node as proposer and target and the
/// key in the Authorization header only; what the show caller approves is collected, run through the node's own path
/// and answered, and completes the node's proposal; what the caller rejects completes it as rejected and nothing runs;
/// a line approved for this node by another app runs once; a desk that refuses (a 4xx) leaves the proposal on the node's own
/// list; a request the desk does not answer is cut only once the sink's own clock passes its deadline, and an offer so cut,
/// stopped, failed, answered with a fault or answered without an id is said as one the desk may hold. Every sink here runs its
/// deadlines on a clock the test moves by hand, so no request is cut however slowly the runner answers.</summary>
public sealed class StagewatchSinkTests
{
    private const string Key = "swk_nodekey0123456789";

    /// <summary>How long a wait on the runner (a loopback round trip, a pull's turn) is given, in ms: the 30 s the waits on a
    /// task here are given. A passing run never waits it out.</summary>
    private const int Patience = 30_000;

    private const string MayHoldIt = "; the desk may hold it)";

    private static Task<bool> Eventually(Func<bool> condition) => OpsRig.Eventually(condition, Patience);

    /// <summary>The journal's rows that name a proposal.</summary>
    private static List<string> Rows(OpsRig rig, string id) =>
        rig.Engine.Journal.Recent(80).Where(r => r.Text.Contains(id, StringComparison.Ordinal)).Select(r => r.Text).ToList();

    [Fact]
    public async Task ProposalsGoToTheDeskAndItsDecisionsComeBack()
    {
        await using var desk = await FakeApprovalDesk.StartAsync();
        var model = new ScriptedModel()
            .Then(ScriptedModel.Propose("c1", "black for the change", "BLACKOUT ON"))
            .Then(ScriptedModel.Say("Asked the show caller."))
            .Then(ScriptedModel.Propose("c2", "freeze the picture", "FREEZE ON"))
            .Then(ScriptedModel.Say("Asked the show caller."));
        var rig = new OpsRig(model: model);
        try
        {
            var self = rig.Engine.Services.Settings.NodeId;
            await rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri($"http://127.0.0.1:{desk.Port}/"),
                Key = Key,
                SelfId = self,
                SelfName = "Stage left",
                PollEvery = TimeSpan.FromMilliseconds(50),
                // Never moved: a request is never cut by its deadline here, however slowly the runner answers.
                Time = new ManualTime(),
            });

            var first = JsonDocument.Parse(await rig.Ok("ASK black please")).RootElement.GetProperty("queued")[0].GetString()!;
            var forwarded = rig.Assistant.Queue.Find(first)!;
            Assert.Equal(ProposalState.Forwarded, forwarded.State);
            Assert.Equal("p1", forwarded.RemoteId);
            var offered = JsonDocument.Parse(desk.Bodies.First()).RootElement;
            Assert.Equal("BLACKOUT ON", offered.GetProperty("line").GetString());
            Assert.Equal("high", offered.GetProperty("risk").GetString());
            Assert.Equal("black for the change", offered.GetProperty("reason").GetString());
            Assert.Equal(("playmap", self), (offered.GetProperty("from").GetProperty("t").GetString(), offered.GetProperty("from").GetProperty("id").GetString()));
            Assert.Equal(self, offered.GetProperty("target").GetProperty("id").GetString());
            // Forwarded is the desk's to decide: the node's own APPROVE does not run it.
            Assert.False((await rig.Run("APPROVE " + first)).Ok);
            Assert.False(rig.Engine.Air.Blackout);

            desk.Decide("p1", "approved", "Caller");
            Assert.True(await Eventually(() => rig.Assistant.Queue.Find(first)!.State == ProposalState.Done), rig.Assistant.Queue.Find(first)!.Summary);
            Assert.True(rig.Engine.Air.Blackout);
            var done = rig.Assistant.Queue.Find(first)!;
            Assert.Equal(true, done.ResultOk);
            Assert.Contains("Caller", done.DecidedBy, StringComparison.Ordinal);
            var (resultId, resultBody) = Assert.Single(desk.Answers);
            Assert.Equal("p1", resultId);
            Assert.True(JsonDocument.Parse(resultBody).RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains(rig.Engine.Journal.Recent(80), r => r.Who is not null && r.Who.Contains("Caller", StringComparison.Ordinal) && r.Text.Contains("black", StringComparison.Ordinal));

            var second = JsonDocument.Parse(await rig.Ok("ASK freeze it")).RootElement.GetProperty("queued")[0].GetString()!;
            desk.Decide("p2", "rejected", "Caller");
            Assert.True(await Eventually(() => rig.Assistant.Queue.Find(second)!.State == ProposalState.Rejected), rig.Assistant.Queue.Find(second)!.Summary);
            Assert.False(rig.Engine.Air.Frozen);
            Assert.Single(desk.Answers);

            // A line another app proposed for this node, approved on the desk, runs once and is answered.
            var other = desk.AddApproved(self, "PATTERN grid");
            Assert.True(await Eventually(() => desk.Answers.Any(r => r.Id == other)));
            Assert.Equal("pattern.grid", rig.Engine.Air.PatternKind);
            await Task.Delay(200);
            Assert.Single(desk.Answers, r => r.Id == other);

            // The key rides in the header on every request and never in a path or a query.
            Assert.All(desk.Authorizations, a => Assert.Equal("Bearer " + Key, a));
            Assert.All(desk.Asked, a => Assert.DoesNotContain(Key, a, StringComparison.Ordinal));
            Assert.DoesNotContain(Key, rig.Engine.State().Json, StringComparison.Ordinal);
            Assert.Contains(desk.Asked, a => a.Contains("target=" + self, StringComparison.Ordinal) && a.Contains("state=approved", StringComparison.Ordinal));
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task TheDeskSeesALinkWithoutItsTokenAndTheNodeRunsItsOwnRecordOnce()
    {
        await using var desk = await FakeApprovalDesk.StartAsync();
        var links = new LinkSpy();
        var rig = new OpsRig(network: links);
        try
        {
            var self = rig.Engine.Services.Settings.NodeId;
            await rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri($"http://127.0.0.1:{desk.Port}/"),
                Key = Key,
                SelfId = self,
                PollEvery = TimeSpan.FromMilliseconds(50),
                // Never moved: a request is never cut by its deadline here, however slowly the runner answers.
                Time = new ManualTime(),
            });
            var proposal = await rig.Assistant.Queue.EnqueueAsync(new ProposalDraft("LINK PATTERNS host secret-token", PlayMap.Core.Wire.VerbCatalog.Find("LINK PATTERNS")!,
                "follow the desk", "an assistant over MCP", "mcp", "mcp/1", 0), CancellationToken.None);
            Assert.Equal(ProposalState.Forwarded, proposal.State);
            var offered = JsonDocument.Parse(Assert.Single(desk.Bodies)).RootElement;
            Assert.Equal("LINK PATTERNS host " + PlayMap.Core.Wire.VerbCatalog.SecretMarker, offered.GetProperty("line").GetString());

            desk.Decide(proposal.RemoteId, "approved", "Caller");
            Assert.True(await Eventually(() => rig.Assistant.Queue.Find(proposal.Id)!.State == ProposalState.Done), rig.Assistant.Queue.Find(proposal.Id)!.Summary);
            Assert.True(await Eventually(() => !links.Patterns.IsEmpty));
            Assert.Equal(("host", "secret-token"), Assert.Single(links.Patterns));

            // Another app's copy of a shown line, approved for this node, is refused: only a record holding the words runs.
            var copy = desk.AddApproved(self, "LINK PATTERNS host " + PlayMap.Core.Wire.VerbCatalog.SecretMarker);
            Assert.True(await Eventually(() => desk.Answers.Any(a => a.Id == copy)));
            Assert.False(JsonDocument.Parse(desk.Answers.Single(a => a.Id == copy).Body).RootElement.GetProperty("ok").GetBoolean());
            Assert.Single(links.Patterns);
            Assert.All(desk.Bodies.Concat(desk.Answers.Select(a => a.Body)), b => Assert.DoesNotContain("secret-token", b, StringComparison.Ordinal));
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    /// <summary>A desk that records an offer and answers late: once the sink's clock passes the deadline the offer is cut and the
    /// proposal stays on the node's list, said as unanswered and possibly held by the desk, never as one the desk did not take; an
    /// answer that comes while the clock has not reached the deadline forwards the proposal, though the runner took longer than the
    /// deadline to give it.</summary>
    [Fact]
    public async Task AnOfferCutAtItsDeadlineMayBeOnTheDeskAndALateAnswerBeforeItForwards()
    {
        await using var desk = await FakeApprovalDesk.StartAsync();
        var clock = new ManualTime();
        var rig = new OpsRig();
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri($"http://127.0.0.1:{desk.Port}/"),
                Key = Key,
                SelfId = rig.Engine.Services.Settings.NodeId,
                PollEvery = TimeSpan.FromMilliseconds(50),
                // Nothing to collect: while nothing is forwarded the pull asks the desk nothing, so the offer's deadline is the only
                // one on the clock when it moves.
                CollectApproved = false,
                Time = clock,
            });
            desk.HoldAnswers(late.Task);
            var offered = rig.Assistant.Queue.EnqueueAsync(Draft("BLACKOUT ON", "BLACKOUT"), CancellationToken.None);
            Assert.True(await Eventually(() => desk.Held.Count == 1), "the desk never recorded the offer");
            Assert.Equal(1, clock.Waiting);
            clock.Advance(StagewatchProposalSink.RequestDeadline - TimeSpan.FromTicks(1));
            Assert.Equal(1, clock.Waiting);
            Assert.False(offered.IsCompleted);
            clock.Advance(TimeSpan.FromTicks(1));
            var cut = await offered.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(ProposalState.Pending, cut.State);
            Assert.Equal("", cut.RemoteId);
            Assert.Contains(rig.Assistant.Queue.Pending, p => p.Id == cut.Id);
            // The sink's words, carried into the queue's journal row for the offer.
            Assert.Contains(rig.Engine.Journal.Recent(80), r => r.Text.Contains(cut.Id, StringComparison.Ordinal)
                && r.Text.Contains("(no answer within 10 s; the desk may hold it)", StringComparison.Ordinal));
            // The desk holds what the node lists as its own: the cut is the answer's, not the desk's refusal.
            Assert.Equal("p1", Assert.Single(desk.Held));

            desk.HoldAnswers(answer.Task);
            var second = rig.Assistant.Queue.EnqueueAsync(Draft("FREEZE ON", "FREEZE"), CancellationToken.None);
            Assert.True(await Eventually(() => desk.Held.Count == 2), "the desk never recorded the second offer");
            // A fixed wait on purpose: the answer comes later on the runner than the deadline, which the sink's clock has not reached.
            await Task.Delay(StagewatchProposalSink.RequestDeadline + TimeSpan.FromMilliseconds(500));
            Assert.False(second.IsCompleted);
            answer.SetResult();
            var forwarded = await second.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(ProposalState.Forwarded, forwarded.State);
            Assert.Equal("p2", forwarded.RemoteId);
        }
        finally
        {
            late.TrySetResult();
            answer.TrySetResult();
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    /// <summary>A pull the desk does not answer is cut once the sink's clock passes the deadline and said as a desk that could not be
    /// read, the next pull asks again, and letting the desk go while that one waits ends the pull without a word.</summary>
    [Fact]
    public async Task APullCutAtItsDeadlineSaysTheDeskCouldNotBeReadAndAStopSaysNothing()
    {
        var clock = new ManualTime();
        var rig = new OpsRig();
        using var desk = new SilentDesk();
        try
        {
            await rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri("http://127.0.0.1:9/"),
                Key = Key,
                SelfId = rig.Engine.Services.Settings.NodeId,
                PollEvery = TimeSpan.FromMilliseconds(50),
                Handler = desk,
                Time = clock,
            });
            var sink = rig.Assistant.ApprovalDesk!;
            Assert.True(await Eventually(() => desk.Asked == 1 && clock.Waiting == 1), $"asked {desk.Asked}, {clock.Waiting} deadlines waiting");
            clock.Advance(StagewatchProposalSink.RequestDeadline - TimeSpan.FromTicks(1));
            Assert.Equal(1, clock.Waiting);
            Assert.Equal("", sink.Problem);
            clock.Advance(TimeSpan.FromTicks(1));
            var cut = $"{sink.Name} could not be read: no answer within 10 s";
            Assert.True(await Eventually(() => sink.Problem.Length > 0), "the cut pull said nothing");
            Assert.Equal(cut, sink.Problem);
            Assert.True(await Eventually(() => desk.Asked == 2 && clock.Waiting == 1), $"asked {desk.Asked}, {clock.Waiting} deadlines waiting");

            await rig.DisposeAsync();
            Assert.Equal(cut, sink.Problem);
            Assert.Null(rig.Assistant.ApprovalDesk);
            Assert.Equal(2, desk.Asked);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    /// <summary>Only a refusal (a 4xx) is said as an offer the desk did not take, by the sink itself, the proposal left on the node's
    /// list. A fault answered (a 5xx) and a success without an id the node can read (none, one past 64 characters, an answer that is
    /// not JSON) leave it there too, said as one the desk may hold: neither says the desk did not record it.</summary>
    [Theory]
    [InlineData(403, "{\"ok\":false,\"error\":\"not this node's key\"}", true, "403 not this node's key")]
    [InlineData(503, "{\"ok\":false,\"error\":\"the desk is restarting\"}", false, "the desk answered 503: the desk is restarting")]
    [InlineData(502, "", false, "the desk answered 502")]
    [InlineData(201, "{\"ok\":true,\"state\":\"pending\"}", false, "the desk answered 201 without an id the node can read")]
    [InlineData(201, "{\"ok\":true,\"id\":\"pqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq\"}", false,
        "the desk answered 201 without an id the node can read")]
    [InlineData(201, "taken", false, "the desk answered 201 without an id the node can read")]
    public async Task OnlyARefusalIsSaidAsNotTakenAndAnAnswerWithoutAnIdMayBeOnTheDesk(int status, string body, bool refused, string words)
    {
        var rig = new OpsRig();
        using var desk = new AnsweringDesk((System.Net.HttpStatusCode)status, body);
        try
        {
            await rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri("http://127.0.0.1:9/"),
                Key = Key,
                SelfId = rig.Engine.Services.Settings.NodeId,
                PollEvery = TimeSpan.FromMilliseconds(50),
                CollectApproved = false,
                Handler = desk,
                Time = new ManualTime(),
            });
            var sink = rig.Assistant.ApprovalDesk!;
            var offered = await rig.Assistant.Queue.EnqueueAsync(Draft("BLACKOUT ON", "BLACKOUT"), CancellationToken.None);
            Assert.Equal(ProposalState.Pending, offered.State);
            Assert.Equal("", offered.RemoteId);
            Assert.Contains(rig.Assistant.Queue.Pending, p => p.Id == offered.Id);
            var rows = Rows(rig, offered.Id);
            var refusal = $"{sink.Name} did not take {offered.Id}: ";
            if (refused)
            {
                // The desk said so: the sink says it did not take the offer, and nothing says the desk may hold it.
                Assert.Equal(refusal + words, sink.Problem);
                Assert.Contains(rows, r => r.Contains(sink.Problem, StringComparison.Ordinal));
                Assert.DoesNotContain(rows, r => r.Contains("may hold it", StringComparison.Ordinal));
            }
            else
            {
                Assert.Equal("", sink.Problem);
                Assert.Contains(rows, r => r.Contains("(" + words + MayHoldIt, StringComparison.Ordinal));
                Assert.DoesNotContain(rows, r => r.Contains(refusal, StringComparison.Ordinal));
            }
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    /// <summary>An offer its caller stops is said as stopped and as one the desk may hold, never as cut: so is one whose stop lands
    /// after its deadline has reached the request but before the request has ended, for the stop is the caller's.</summary>
    [Fact]
    public async Task AnOfferItsCallerStopsIsSaidAsStoppedThoughItsDeadlineCameFirst()
    {
        const string Stopped = "(stopped before the desk answered" + MayHoldIt;
        var clock = new ManualTime();
        var rig = new OpsRig();
        using var desk = new HeldOfferDesk();
        using var afterTheDeadline = new CancellationTokenSource();
        using var beforeIt = new CancellationTokenSource();
        try
        {
            await rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri("http://127.0.0.1:9/"),
                Key = Key,
                SelfId = rig.Engine.Services.Settings.NodeId,
                PollEvery = TimeSpan.FromMilliseconds(50),
                // Nothing to collect and nothing forwarded: the pull asks the desk nothing, so the offer's deadline is the only one
                // on the clock.
                CollectApproved = false,
                Handler = desk,
                Time = clock,
            });

            // The deadline reaches the request first and the desk holds the request's end; the caller's stop lands before it ends.
            var first = rig.Assistant.Queue.EnqueueAsync(Draft("BLACKOUT ON", "BLACKOUT"), afterTheDeadline.Token);
            Assert.True(await Eventually(() => desk.Held == 1 && clock.Waiting == 1), $"held {desk.Held}, {clock.Waiting} deadlines waiting");
            clock.Advance(StagewatchProposalSink.RequestDeadline);
            Assert.True(await Eventually(() => desk.Cancelled == 1), "the deadline never reached the request");
            Assert.False(first.IsCompleted);
            await afterTheDeadline.CancelAsync();
            desk.Open();
            var late = await first.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(ProposalState.Pending, late.State);
            Assert.Contains(Rows(rig, late.Id), r => r.Contains(Stopped, StringComparison.Ordinal));

            // Stopped while its deadline waits: the same words, and the deadline is let go with the request.
            var second = rig.Assistant.Queue.EnqueueAsync(Draft("FREEZE ON", "FREEZE"), beforeIt.Token);
            Assert.True(await Eventually(() => desk.Held == 2 && clock.Waiting == 1), $"held {desk.Held}, {clock.Waiting} deadlines waiting");
            await beforeIt.CancelAsync();
            var early = await second.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(ProposalState.Pending, early.State);
            Assert.Contains(Rows(rig, early.Id), r => r.Contains(Stopped, StringComparison.Ordinal));
            Assert.Equal(0, clock.Waiting);
            Assert.All(Rows(rig, late.Id).Concat(Rows(rig, early.Id)), r => Assert.DoesNotContain(StagewatchProposalSink.NoAnswer, r, StringComparison.Ordinal));
        }
        finally
        {
            desk.Open();
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    private static ProposalDraft Draft(string line, string verb) =>
        new(line, PlayMap.Core.Wire.VerbCatalog.Find(verb)!, "for the change", "an assistant over MCP", "mcp", "mcp/1", 0);

    [Fact]
    public async Task ADeskThatDoesNotAnswerLeavesTheProposalOnTheNodesOwnList()
    {
        var model = new ScriptedModel()
            .Then(ScriptedModel.Propose("c1", "black", "BLACKOUT ON"))
            .Then(ScriptedModel.Say("Queued."));
        var rig = new OpsRig(model: model);
        try
        {
            // Nothing listens on this port.
            using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            await rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri($"http://127.0.0.1:{port}/"),
                Key = Key,
                SelfId = rig.Engine.Services.Settings.NodeId,
                Time = new ManualTime(),
            });
            var id = JsonDocument.Parse(await rig.Ok("ASK black")).RootElement.GetProperty("queued")[0].GetString()!;
            Assert.Equal(ProposalState.Pending, rig.Assistant.Queue.Find(id)!.State);
            // The failure is said with its own words and as one the desk may hold: a failure does not say the offer never reached it.
            Assert.Contains(Rows(rig, id), r => r.Contains("(no answer (", StringComparison.Ordinal) && r.Contains(MayHoldIt, StringComparison.Ordinal));
            await rig.Ok("APPROVE " + id);
            Assert.True(rig.Engine.Air.Blackout);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    /// <summary>A refusal that lands as the stop is taken is the stop's: the pull ends without naming it, and letting the desk go
    /// (with it the assistant and the node's services) throws nothing, once or twice. A refusal before the stop is the desk's and
    /// is said.</summary>
    [Fact]
    public async Task ARefusalThatLandsAsTheStopIsTakenEndsThePullAndIsNotThrownAtWhoeverLetsTheDeskGo()
    {
        var spy = new PullTokenSpy();
        var rig = new OpsRig(network: spy);
        using var desk = new LateRefusalDesk(spy);
        try
        {
            await rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri("http://127.0.0.1:9/"),
                Key = Key,
                SelfId = rig.Engine.Services.Settings.NodeId,
                PollEvery = TimeSpan.FromMilliseconds(50),
                Handler = desk,
                Time = new ManualTime(),
            });
            var sink = rig.Assistant.ApprovalDesk!;
            await spy.Token.WaitAsync(TimeSpan.FromSeconds(30));
            await desk.Held.WaitAsync(TimeSpan.FromSeconds(30));
            var early = $"{sink.Name} could not be read: {LateRefusalDesk.EarlyWords}";
            Assert.Equal(early, sink.Problem);

            await rig.DisposeAsync();
            Assert.True(desk.RaisedAfterTheStop && desk.RaisedBeforeTheRequestsCancel,
                $"the late refusal was raised after the stop: {desk.RaisedAfterTheStop}, before the request's cancel: {desk.RaisedBeforeTheRequestsCancel}");
            Assert.Equal(early, sink.Problem);
            Assert.Null(rig.Assistant.ApprovalDesk);
            await sink.DisposeAsync();
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    /// <summary>A pull that fails as no pull expects ends, said at once on the sink and in the journal and written with its trace to
    /// the engine's log, and letting the desk go throws nothing.</summary>
    [Fact]
    public async Task APullThatFailsAsNoPullExpectsEndsSaidAndIsNotThrownAtWhoeverLetsTheDeskGo()
    {
        var log = new KeptFaults();
        var rig = new OpsRig(assistantLog: log);
        using var desk = new BrokenClientDesk();
        try
        {
            await rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri("http://127.0.0.1:9/"),
                Key = Key,
                SelfId = rig.Engine.Services.Settings.NodeId,
                PollEvery = TimeSpan.FromMilliseconds(50),
                Handler = desk,
                Time = new ManualTime(),
            });
            var sink = rig.Assistant.ApprovalDesk!;
            // Said on the sink and written to the engine's log first, then in the journal: the journal's row is the last of the three.
            Assert.True(await Eventually(() => rig.Engine.Journal.Recent(80).Any(r => r.Text.Contains(BrokenClientDesk.Words, StringComparison.Ordinal))),
                $"the sink says \"{sink.Problem}\"");
            Assert.Contains(BrokenClientDesk.Words, sink.Problem, StringComparison.Ordinal);
            var (level, words, fault) = Assert.Single(log.Entries);
            Assert.Equal(LogLevel.Warning, level);
            Assert.Contains(sink.Name, words, StringComparison.Ordinal);
            var thrown = Assert.IsType<InvalidOperationException>(fault);
            Assert.Equal(BrokenClientDesk.Words, thrown.Message);
            Assert.Contains(typeof(BrokenClientDesk).FullName!, thrown.StackTrace ?? "", StringComparison.Ordinal);
            await Task.Delay(200);
            Assert.Equal(1, desk.Asked);
            Assert.Single(rig.Engine.Journal.Recent(80), r => r.Text.Contains(BrokenClientDesk.Words, StringComparison.Ordinal));

            await rig.DisposeAsync();
            Assert.Contains(BrokenClientDesk.Words, sink.Problem, StringComparison.Ordinal);
            Assert.Null(rig.Assistant.ApprovalDesk);
            Assert.Single(log.Entries);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    /// <summary>A change of desk while the old desk's call is refused as its stop is taken completes: the new desk is the one the node
    /// reads, the old one names nothing new, and nothing is thrown at whoever changed the desk.</summary>
    [Fact]
    public async Task AChangeOfDeskWhileTheOldDesksCallIsRefusedAsItsStopIsTakenCompletes()
    {
        var spy = new PullTokenSpy();
        var rig = new OpsRig(network: spy);
        using var desk = new LateRefusalDesk(spy);
        using var next = new QuietDesk();
        try
        {
            await rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri("http://127.0.0.1:9/"),
                Key = Key,
                SelfId = rig.Engine.Services.Settings.NodeId,
                PollEvery = TimeSpan.FromMilliseconds(50),
                Handler = desk,
                Time = new ManualTime(),
            });
            var old = rig.Assistant.ApprovalDesk!;
            await spy.Token.WaitAsync(TimeSpan.FromSeconds(30));
            await desk.Held.WaitAsync(TimeSpan.FromSeconds(30));

            await rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri("http://127.0.0.1:10/"),
                Key = Key,
                SelfId = rig.Engine.Services.Settings.NodeId,
                PollEvery = TimeSpan.FromMilliseconds(50),
                Handler = next,
                Time = new ManualTime(),
            });
            Assert.True(desk.RaisedAfterTheStop && desk.RaisedBeforeTheRequestsCancel,
                $"the late refusal was raised after the stop: {desk.RaisedAfterTheStop}, before the request's cancel: {desk.RaisedBeforeTheRequestsCancel}");
            Assert.Equal($"{old.Name} could not be read: {LateRefusalDesk.EarlyWords}", old.Problem);
            var used = rig.Assistant.ApprovalDesk;
            Assert.NotNull(used);
            Assert.Equal("Stagewatch at 127.0.0.1:10", used.Name);
            Assert.True(await Eventually(() => next.Asked >= 2), $"the new desk was asked {next.Asked} times");
            Assert.Equal("", used.Problem);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    /// <summary>A change of desk whose old desk's stop meets a callback that throws completes once the old desk's pull has ended: the
    /// fault is written with its trace to the engine's log, nothing is thrown at whoever changed the desk, the old desk names nothing,
    /// and the new desk is the one read.</summary>
    [Fact]
    public async Task AChangeOfDeskWhoseOldStopMeetsAThrowingCallbackCompletesOnceTheOldPullHasEnded()
    {
        var log = new KeptFaults();
        var rig = new OpsRig(assistantLog: log);
        using var desk = new ThrowingStopDesk();
        using var next = new QuietDesk();
        try
        {
            await rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri("http://127.0.0.1:9/"),
                Key = Key,
                SelfId = rig.Engine.Services.Settings.NodeId,
                PollEvery = TimeSpan.FromMilliseconds(50),
                Handler = desk,
                Time = new ManualTime(),
            });
            var old = rig.Assistant.ApprovalDesk!;
            await desk.Held.WaitAsync(TimeSpan.FromSeconds(30));

            var change = rig.Assistant.UseApprovalDeskAsync(new StagewatchSinkOptions
            {
                BaseUri = new Uri("http://127.0.0.1:10/"),
                Key = Key,
                SelfId = rig.Engine.Services.Settings.NodeId,
                PollEvery = TimeSpan.FromMilliseconds(50),
                Handler = next,
                Time = new ManualTime(),
            });
            // The old desk's collect is held past its stop, so the change waits for it.
            await Task.WhenAny(change, Task.Delay(500));
            Assert.False(change.IsCompleted, $"the change ended before the old desk's pull: {change.Exception?.GetBaseException().Message}");
            Assert.False(desk.Ended);
            desk.Open();
            await change.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(desk.Ended);
            Assert.Equal("", old.Problem);
            var used = rig.Assistant.ApprovalDesk;
            Assert.NotNull(used);
            Assert.Equal("Stagewatch at 127.0.0.1:10", used.Name);
            Assert.True(await Eventually(() => next.Asked >= 2), $"the new desk was asked {next.Asked} times");
            var (level, words, fault) = Assert.Single(log.Entries);
            Assert.Equal(LogLevel.Warning, level);
            Assert.Contains(old.Name, words, StringComparison.Ordinal);
            var thrown = Assert.IsType<InvalidOperationException>(Assert.Single(Leaves(fault)));
            Assert.Equal(ThrowingStopDesk.Words, thrown.Message);
            Assert.Contains(typeof(ThrowingStopDesk).FullName!, thrown.StackTrace ?? "", StringComparison.Ordinal);
        }
        finally
        {
            desk.Open();
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    /// <summary>The exceptions an aggregate carries, however deep.</summary>
    private static IEnumerable<Exception> Leaves(Exception? e) => e switch
    {
        null => [],
        AggregateException a => a.InnerExceptions.SelectMany(Leaves),
        _ => [e],
    };
}
