using System.Net;
using System.Text;

namespace PlayMap.Assistant.Tests;

/// <summary>A model that answers from a script, one reply per call, and keeps every request it was sent.</summary>
internal sealed class ScriptedProvider : IAssistantProvider
{
    private readonly Queue<Func<AssistantRequest, AssistantReply>> _script = new();

    public ScriptedProvider(AssistantProviderKind kind = AssistantProviderKind.Claude, int validationRetries = 2)
    {
        Kind = kind;
        ValidationRetries = validationRetries;
    }

    public AssistantProviderKind Kind { get; }

    public string Model => "scripted-model";

    public int ValidationRetries { get; }

    public List<AssistantRequest> Requests { get; } = [];

    /// <summary>When the script runs out, every further call answers this (null: end the turn).</summary>
    public Func<AssistantRequest, AssistantReply>? Forever { get; set; }

    public ScriptedProvider Then(Func<AssistantRequest, AssistantReply> reply)
    {
        _script.Enqueue(reply);
        return this;
    }

    public ScriptedProvider Then(AssistantReply reply) => Then(_ => reply);

    public Task<AssistantReply> CompleteAsync(AssistantRequest request, Action<string>? onText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Add(request);
        var next = _script.Count > 0 ? _script.Dequeue() : Forever ?? (_ => Say("Done."));
        var reply = next(request);
        foreach (var t in reply.Parts.OfType<TextPart>())
            onText?.Invoke(t.Text);
        return Task.FromResult(reply);
    }

    public void Dispose()
    {
        // Nothing held.
    }

    public static AssistantReply Say(string text) =>
        new([new TextPart(text)], StopKind.EndTurn, new UsageCounts(100, 20, 0, 0), "scripted-model");

    public static AssistantReply Call(params ToolCallPart[] calls) =>
        new([.. calls], StopKind.ToolUse, new UsageCounts(100, 20, 90, 0), "scripted-model");

    public static ToolCallPart Tool(string id, string name, string args = "{}") => new(id, name, args);

    public static ToolCallPart Propose(string id, string reason, params string[] lines) =>
        new(id, AssistantTools.ProposeCommands,
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["lines"] = lines, ["reason"] = reason }));
}

/// <summary>An HTTP server that is not there: it records the request and answers a canned body.</summary>
internal sealed class CannedHandler : HttpMessageHandler
{
    public CannedHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        Body = body;
        Status = status;
    }

    public string Body { get; }

    public HttpStatusCode Status { get; }

    public Exception? Throw { get; set; }

    public HttpRequestMessage? Request { get; private set; }

    public string RequestBody { get; private set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        RequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        if (Throw is not null)
            throw Throw;
        return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
    }
}
