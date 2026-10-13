using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PlayMap.Assistant;
using PlayMap.Engine.Assistant;

namespace PlayMap.Engine.Tests;

/// <summary>A Stagewatch approval desk as Show Bus §11 has it (Stagewatch's own ProposalDesk, played small): POST
/// /api/proposals takes a proposal and answers its id; GET ?target=&lt;id&gt;&amp;state=approved hands over what was
/// approved for a node (once); POST /api/proposals/&lt;id&gt;/result records what it did; GET ?from=&lt;id&gt; lists a
/// proposer's own with their states. Every request's path, query and Authorization are kept.</summary>
internal sealed class FakeApprovalDesk : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, JsonObject> _proposals = new(StringComparer.Ordinal);
    private int _next;

    private FakeApprovalDesk(WebApplication app) => _app = app;

    public int Port { get; private set; }

    public ConcurrentQueue<string> Asked { get; } = new();

    public ConcurrentQueue<string> Authorizations { get; } = new();

    public ConcurrentQueue<string> Bodies { get; } = new();

    public ConcurrentQueue<(string Id, string Body)> Answers { get; } = new();

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
            lock (desk._gate)
            {
                id = "p" + (++desk._next).ToString(System.Globalization.CultureInfo.InvariantCulture);
                p["id"] = id;
                p["state"] = "pending";
                desk._proposals[id] = p;
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

/// <summary>The node's proposals on a Stagewatch approval desk: offered with the node as proposer and target and the
/// key in the Authorization header only; what the show caller approves is collected, run through the node's own path
/// and answered, and completes the node's proposal; what the caller rejects completes it as rejected and nothing runs;
/// a line approved for this node by another app runs once; a desk that refuses leaves the proposal on the node's own
/// list.</summary>
public sealed class StagewatchSinkTests
{
    private const string Key = "swk_nodekey0123456789";

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
            Assert.True(await OpsRig.Eventually(() => rig.Assistant.Queue.Find(first)!.State == ProposalState.Done), rig.Assistant.Queue.Find(first)!.Summary);
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
            Assert.True(await OpsRig.Eventually(() => rig.Assistant.Queue.Find(second)!.State == ProposalState.Rejected), rig.Assistant.Queue.Find(second)!.Summary);
            Assert.False(rig.Engine.Air.Frozen);
            Assert.Single(desk.Answers);

            // A line another app proposed for this node, approved on the desk, runs once and is answered.
            var other = desk.AddApproved(self, "PATTERN grid");
            Assert.True(await OpsRig.Eventually(() => desk.Answers.Any(r => r.Id == other)));
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
            });
            var proposal = await rig.Assistant.Queue.EnqueueAsync(new ProposalDraft("LINK PATTERNS host secret-token", PlayMap.Core.Wire.VerbCatalog.Find("LINK PATTERNS")!,
                "follow the desk", "an assistant over MCP", "mcp", "mcp/1", 0), CancellationToken.None);
            Assert.Equal(ProposalState.Forwarded, proposal.State);
            var offered = JsonDocument.Parse(Assert.Single(desk.Bodies)).RootElement;
            Assert.Equal("LINK PATTERNS host " + PlayMap.Core.Wire.VerbCatalog.SecretMarker, offered.GetProperty("line").GetString());

            desk.Decide(proposal.RemoteId, "approved", "Caller");
            Assert.True(await OpsRig.Eventually(() => rig.Assistant.Queue.Find(proposal.Id)!.State == ProposalState.Done), rig.Assistant.Queue.Find(proposal.Id)!.Summary);
            Assert.True(await OpsRig.Eventually(() => !links.Patterns.IsEmpty));
            Assert.Equal(("host", "secret-token"), Assert.Single(links.Patterns));

            // Another app's copy of a shown line, approved for this node, is refused: only a record holding the words runs.
            var copy = desk.AddApproved(self, "LINK PATTERNS host " + PlayMap.Core.Wire.VerbCatalog.SecretMarker);
            Assert.True(await OpsRig.Eventually(() => desk.Answers.Any(a => a.Id == copy)));
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
            });
            var id = JsonDocument.Parse(await rig.Ok("ASK black")).RootElement.GetProperty("queued")[0].GetString()!;
            Assert.Equal(ProposalState.Pending, rig.Assistant.Queue.Find(id)!.State);
            await rig.Ok("APPROVE " + id);
            Assert.True(rig.Engine.Air.Blackout);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
