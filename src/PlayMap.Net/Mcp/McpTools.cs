using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlayMap.Core.Wire;
using PlayMap.Net.Commands;

namespace PlayMap.Net.Mcp;

/// <summary>PlayMap at <c>/mcp</c> (Model Context Protocol): the node's facts as tools and resources (STATE,
/// context, project, the vocabulary) and one tool that runs a line of the vocabulary — through the same path as the
/// wire, so a verb means the same from an assistant as from Patterns. A low or review verb runs on the assistant's
/// word; a high-risk verb (and any verb the catalogue does not know, which counts as high) runs only when the
/// approval policy allows it, and is otherwise queued for a person or refused with the reason — never run.</summary>
public sealed class McpTools(CommandRunner runner, IApprovalPolicy policy, ILogger log)
{
    public const string AddressClaim = "playmap.address";
    public const string LoopbackClaim = "playmap.loopback";

    private static readonly JsonElement NoArguments = Schema("""{"type":"object","properties":{}}""");

    private static readonly JsonElement RunArguments = Schema("""
        {"type":"object","properties":{
          "line":{"type":"string","description":"One line of PlayMap's vocabulary exactly as on the wire, e.g. \"VIDEO SEEK 83\" or \"PLAYLIST GO Show #2\" (list_verbs has every verb with its syntax and risk)"},
          "reason":{"type":"string","description":"Why, in a sentence: shown to the operator and journalled with the result"}},
         "required":["line","reason"]}
        """);

    private long _sessions;

    public static readonly IReadOnlyList<(string Uri, string Name, string Description)> Resources =
    [
        ("playmap://state", "state", "The node's STATE: air, video, playlists, outputs, media, stage, sync, links, peers (compact JSON)"),
        ("playmap://context", "context", "Show Bus context: light, headline, items, recent, the commands that may be proposed with their risk"),
        ("playmap://project", "project", "The whole project document (JSON)"),
    ];

    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken ct) =>
        ValueTask.FromResult(new ListToolsResult
        {
            Tools =
            [
                Tool("get_state", "The node's STATE now: what is on the air, every playlist's transport, outputs' health, sync, links. Read it before acting.", NoArguments, readOnly: true),
                Tool("get_context", "Show Bus context: the node's light and headline computed from facts, its items, recent journal lines, and the verbs that may be proposed with their risk.", NoArguments, readOnly: true),
                Tool("get_project", "The whole project document: media, playlists, generators, canvases, outputs, stage, projectors, surfaces.", NoArguments, readOnly: true),
                Tool("list_verbs", "PlayMap's vocabulary: every verb with its syntax, what it does, its risk (low, review, high) and whether it changes the show.", NoArguments, readOnly: true),
                Tool("run_command", "Runs one line of the vocabulary and answers its receipt (applied or accepted, the effect, or why it was refused). High-risk verbs (anything that changes the air) run only when the operator allows assistants to act; otherwise they are queued for approval or refused.", RunArguments, readOnly: false),
            ],
        });

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = request.Params;
        var endpoint = runner.Endpoint;
        switch (p?.Name)
        {
            case "get_state":
                return Text(endpoint.State().Json, error: false);
            case "get_context":
                return Text(endpoint.Context(), error: false);
            case "get_project":
                return Text(endpoint.ProjectJson().Json, error: false);
            case "list_verbs":
                return Text(VerbJson.All, error: false);
            case "run_command":
                return await RunAsync(Arg(p, "line"), Arg(p, "reason"), request.User, ct);
            default:
                return Text("no such tool: " + p?.Name + " — list_tools names them", error: true);
        }
    }

    public ValueTask<ListResourcesResult> ListResourcesAsync(RequestContext<ListResourcesRequestParams> request, CancellationToken ct) =>
        ValueTask.FromResult(new ListResourcesResult
        {
            Resources = Resources.Select(r => new Resource { Uri = r.Uri, Name = r.Name, Description = r.Description, MimeType = "application/json" }).ToList(),
        });

    public ValueTask<ReadResourceResult> ReadResourceAsync(RequestContext<ReadResourceRequestParams> request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.Params?.Uri ?? "";
        var endpoint = runner.Endpoint;
        var text = uri switch
        {
            "playmap://state" => endpoint.State().Json,
            "playmap://context" => endpoint.Context(),
            "playmap://project" => endpoint.ProjectJson().Json,
            _ => throw new McpProtocolException("no such resource: " + uri + " — playmap://state, playmap://context, playmap://project", McpErrorCode.ResourceNotFound),
        };
        return ValueTask.FromResult(new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = uri, MimeType = "application/json", Text = text }],
        });
    }

    private async ValueTask<CallToolResult> RunAsync(string text, string reason, ClaimsPrincipal? user, CancellationToken ct)
    {
        if (!WireGrammar.TryParse(text, out var line, out var error))
            return Text(error!.Reason, error: true);
        if (line.Verb is "HELLO" or "NONCE" or "AUTH" or "AUTH-PROOF" or "SUBSCRIBE")
            return Text(WireWords.WireOnly, error: true);
        var verb = VerbCatalog.Match(line)?.Spec;
        if ((verb?.Risk ?? VerbRisk.High) == VerbRisk.High)
        {
            var verdict = await policy.DecideAsync(verb, line.Raw, reason, ct);
            switch (verdict.Decision)
            {
                case ApprovalDecision.Queued:
                    log.LogInformation("An assistant's {Verb} waits for approval ({Proposal}).", verb?.Name ?? "line", verdict.ProposalId);
                    return Text(JsonText.Write(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "receipt");
                        w.WriteBoolean("ok", true);
                        w.WriteString("stage", "queued");
                        w.WriteString("effect", "not run: " + verdict.Words);
                        w.WriteString("proposal", verdict.ProposalId);
                        w.WriteEndObject();
                    }), error: false);
                case ApprovalDecision.Refused:
                    return Text(verdict.Words, error: true);
            }
        }
        var address = user?.FindFirst(AddressClaim)?.Value ?? "?";
        var loopback = user?.FindFirst(LoopbackClaim)?.Value == "1";
        var session = new SessionInfo("m" + Interlocked.Increment(ref _sessions).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "mcp", address, loopback, "MCP", Paired: true);
        var reply = await runner.RunAsync(line, session, ct);
        log.LogInformation("An assistant ran {Verb} over MCP: {Outcome} ({Reason}).", verb?.Name ?? line.Verb, reply.Ok ? "ok" : "refused", reason);
        return Text(CommandRunner.ReceiptJson(line.RequestId, reply), error: !reply.Ok);
    }

    private static string Arg(CallToolRequestParams p, string name) =>
        p.Arguments is { } args && args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static Tool Tool(string name, string description, JsonElement schema, bool readOnly) => new()
    {
        Name = name,
        Description = description,
        InputSchema = schema,
        Annotations = new ToolAnnotations { ReadOnlyHint = readOnly, DestructiveHint = !readOnly, OpenWorldHint = false },
    };

    private static CallToolResult Text(string text, bool error) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = error,
    };

    private static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
