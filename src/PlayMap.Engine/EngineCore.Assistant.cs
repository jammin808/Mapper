using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Core.Wire;
using PlayMap.Engine.Assistant;

namespace PlayMap.Engine;

/// <summary>The assistant verbs: ASK runs one request of the client's conversation and answers with the model's
/// words and every line it ran or queued; PROPOSALS lists what waits; APPROVE and REJECT are a person's words — never
/// an assistant's, never an approval's own — and APPROVE answers with the node's reply to the line it ran.</summary>
public sealed partial class EngineCore
{
    private async Task<CommandReply> AssistantVerbAsync(VerbSpec spec, List<string> args, WireLine line, SessionInfo session, CancellationToken ct)
    {
        if (Services.Assistant is not { } assistant)
            return CommandReply.Fail("there is no assistant on this node");
        switch (spec.Name)
        {
            case "ASK":
                return await AskAsync(assistant, line, session, ct).ConfigureAwait(false);
            case "PROPOSALS":
                return Proposals(assistant, args);
            case "APPROVE":
                return await ApproveAsync(assistant, args, session, ct).ConfigureAwait(false);
            case "REJECT":
                return Reject(assistant, args, session);
            case "ASSISTANT STATUS":
                return CommandReply.Success(AssistantStatusJson(assistant));
            case "ASSISTANT SET":
                return SetAssistant(assistant, args, session);
            default:
                return CommandReply.Fail($"{spec.Name} is not available on this node yet");
        }
    }

    private async Task<CommandReply> AskAsync(AssistantService assistant, WireLine line, SessionInfo session, CancellationToken ct)
    {
        if (IsAssistantTransport(session))
            return CommandReply.Fail("an assistant's line cannot ask the assistant — a person asks");
        var question = line.RestAfter(1);
        if (question.Length == 0)
            return CommandReply.Fail("ASK <words>");
        var (conversation, result) = await assistant.AskAsync(session, question, ct).ConfigureAwait(false);
        if (result.Outcome == TurnOutcome.NotSent)
            return CommandReply.Fail(result.Text);
        var redactor = assistant.Redactor();
        var payload = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("conversation", conversation?.Session.Id ?? "");
            w.WriteString("outcome", AssistantService.OutcomeWord(result.Outcome));
            w.WriteString("text", redactor.Redact(result.Text));
            w.WriteString("status", redactor.Redact(result.Status));
            w.WriteStartArray("lines");
            foreach (var l in result.Lines)
            {
                w.WriteStartObject();
                w.WriteString("line", redactor.Redact(VerbCatalog.Redacted(l.Line)));
                w.WriteString("verb", l.Verb);
                w.WriteString("risk", Core.Bus.ShowContext.RiskWord(l.Risk));
                w.WriteString("status", l.StatusWord);
                w.WriteString("reply", redactor.Redact(l.Reply));
                if (l.ProposalId is not null)
                    w.WriteString("proposal", l.ProposalId);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("queued");
            foreach (var q in result.Queued)
                w.WriteStringValue(q);
            w.WriteEndArray();
            w.WriteNumber("modelCalls", result.ModelCalls);
            w.WriteEndObject();
        });
        var effect = "the assistant " + AssistantService.OutcomeWord(result.Outcome) + (result.Status.Length > 0 ? ": " + redactor.Redact(result.Status) : "");
        return CommandReply.Success(payload, effect);
    }

    private static CommandReply Proposals(AssistantService assistant, List<string> args)
    {
        var all = args.Count > 0 && args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase);
        var list = all ? assistant.Queue.All : assistant.Queue.All.Where(p => p.IsOpen).ToList();
        var redactor = assistant.Redactor();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartArray();
            foreach (var p in list)
                AssistantService.WriteProposal(w, p, redactor);
            w.WriteEndArray();
        }));
    }

    private async Task<CommandReply> ApproveAsync(AssistantService assistant, List<string> args, SessionInfo session, CancellationToken ct)
    {
        if (IsAssistantTransport(session))
            return CommandReply.Fail("a person approves — an assistant's line, or an approval's own, never does");
        if (args.Count != 1)
            return CommandReply.Fail("APPROVE <proposal>");
        Proposal decided;
        try
        {
            decided = await assistant.Queue.ApproveAsync(args[0], Who(session), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return CommandReply.Fail(ex.Message);
        }
        var redactor = assistant.Redactor();
        var payload = JsonOut(w => AssistantService.WriteProposal(w, decided, redactor));
        var ran = decided.ResultOk switch
        {
            true => "it ran: " + redactor.Redact(decided.ResultWords),
            false => "the node refused it: " + redactor.Redact(decided.ResultWords),
            _ => redactor.Redact(decided.ResultWords),
        };
        return CommandReply.Success(payload, $"approved {decided.Id} ({redactor.Redact(decided.Shown)}) — {ran}");
    }

    private static CommandReply Reject(AssistantService assistant, List<string> args, SessionInfo session)
    {
        if (IsAssistantTransport(session))
            return CommandReply.Fail("a person rejects — an assistant's line, or an approval's own, never does");
        if (args.Count < 1)
            return CommandReply.Fail("REJECT <proposal> [<why>]");
        try
        {
            var p = assistant.Queue.Reject(args[0], Who(session), args.Count > 1 ? string.Join(' ', args.Skip(1)) : null);
            var redactor = assistant.Redactor();
            return CommandReply.Success(JsonOut(w => AssistantService.WriteProposal(w, p, redactor)), $"rejected {p.Id} ({redactor.Redact(p.Shown)}) — it did not run");
        }
        catch (InvalidOperationException ex)
        {
            return CommandReply.Fail(ex.Message);
        }
    }

    /// <summary>ASSISTANT STATUS (and STATE.ai.assistant): the settings in force and whether a key is set — never the key.</summary>
    private static string AssistantStatusJson(AssistantService assistant) => JsonOut(w => WriteAssistantStatus(w, assistant));

    internal static void WriteAssistantStatus(Utf8JsonWriter w, AssistantService assistant)
    {
        var s = assistant.Settings;
        w.WriteStartObject();
        w.WriteBoolean("enabled", s.Enabled);
        w.WriteString("provider", JsonNamingPolicy.CamelCase.ConvertName(s.Provider.ToString()));
        w.WriteString("model", s.Model);
        w.WriteString("effort", s.Effort.ToString().ToLowerInvariant());
        w.WriteString("baseUrl", s.BaseUrl);
        w.WriteBoolean("runLowWithoutAsking", s.RunLowWithoutAsking);
        w.WriteBoolean("runReviewWithoutAsking", s.RunReviewWithoutAsking);
        w.WriteBoolean("keySet", assistant.KeySet);
        w.WriteBoolean("kept", assistant.Persists);
        w.WriteEndObject();
    }

    /// <summary>ASSISTANT SET &lt;field&gt; &lt;value&gt;: one setting, from a person on this machine or the node's paired desk
    /// — never an assistant, an MCP client or an approval of one (an assistant does not set its own leash). A value
    /// that changes nothing is refused; the setting is kept before it is put in force.</summary>
    private static CommandReply SetAssistant(AssistantService assistant, List<string> args, SessionInfo session)
    {
        const string Syntax = "ASSISTANT SET enabled|provider|model|effort|baseUrl|runLowWithoutAsking|runReviewWithoutAsking <value>";
        if (IsAssistantTransport(session))
            return CommandReply.Fail("an assistant never changes its own settings — a person does, at the node or its paired desk");
        if (!session.IsLoopback && session.Transport != "desk")
            return CommandReply.Fail("the assistant's settings are set from this machine or its paired desk only");
        if (args.Count != 2)
            return CommandReply.Fail(Syntax);
        var s = assistant.Settings;
        var value = args[1];
        AssistantSettings next;
        string words;
        switch (args[0].ToUpperInvariant())
        {
            case "ENABLED":
                if (!TrySwitch(value, out var on))
                    return CommandReply.Fail("enabled takes on or off");
                next = s with { Enabled = on };
                words = on ? "the assistant is on" : "the assistant is off";
                break;
            case "PROVIDER":
                AssistantProviderKind? kind = value.ToUpperInvariant() switch
                {
                    "CLAUDE" => AssistantProviderKind.Claude,
                    "OPENAICOMPATIBLE" or "LOCAL" => AssistantProviderKind.OpenAiCompatible,
                    "NONE" => AssistantProviderKind.None,
                    _ => null,
                };
                if (kind is not { } k)
                    return CommandReply.Fail("provider is claude, openAiCompatible (a local model) or none");
                // A provider's model names are its own: the default model goes with Claude, a local one is named next.
                next = s with { Provider = k, Model = k == AssistantProviderKind.Claude && s.Provider != k ? AssistantSettings.DefaultClaudeModel : s.Model };
                words = $"the assistant's provider is {JsonNamingPolicy.CamelCase.ConvertName(k.ToString())}";
                break;
            case "MODEL":
                if (value.Length is 0 or > 200)
                    return CommandReply.Fail("model is the model's name as its provider knows it (1 to 200 characters)");
                next = s with { Model = value };
                words = $"the assistant's model is {value}";
                break;
            case "EFFORT":
                if (!Enum.TryParse<AssistantEffort>(value, ignoreCase: true, out var effort) || !Enum.IsDefined(effort) || int.TryParse(value, out _))
                    return CommandReply.Fail("effort is low, medium, high, xhigh or max");
                next = s with { Effort = effort };
                words = $"the assistant's effort is {effort.ToString().ToLowerInvariant()}";
                break;
            case "BASEURL":
                var url = value.Length == 0 || value == "-" ? "" : value;
                if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                    return CommandReply.Fail("baseUrl is an http(s) address, or \"\" for the provider's own");
                next = s with { BaseUrl = url };
                words = url.Length > 0 ? $"the assistant's endpoint is {url}" : "the assistant uses its provider's own endpoint";
                break;
            case "RUNLOWWITHOUTASKING":
                if (!TrySwitch(value, out var low))
                    return CommandReply.Fail("runLowWithoutAsking takes on or off");
                next = s with { RunLowWithoutAsking = low };
                words = low ? "low-risk lines run on the assistant's word" : "low-risk lines wait for a person";
                break;
            case "RUNREVIEWWITHOUTASKING":
                if (!TrySwitch(value, out var review))
                    return CommandReply.Fail("runReviewWithoutAsking takes on or off");
                next = s with { RunReviewWithoutAsking = review };
                words = review ? "review lines run on the assistant's word" : "review lines wait for a person";
                break;
            default:
                return CommandReply.Fail(Syntax);
        }
        if (next.Normalised() == s)
            return CommandReply.Fail($"already so — {words}");
        try
        {
            assistant.ApplySettings(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandReply.Fail($"the setting could not be kept ({ex.Message}) — nothing changed");
        }
        return CommandReply.Success(AssistantStatusJson(assistant), assistant.Persists ? words : words + " (until the node stops: it keeps no settings file)");
    }

    private static bool TrySwitch(string word, out bool on)
    {
        on = word.ToUpperInvariant() is "ON" or "TRUE" or "1" or "YES";
        return on || word.ToUpperInvariant() is "OFF" or "FALSE" or "0" or "NO";
    }

    private static string Who(SessionInfo session) =>
        session.ClientName.Length > 0 ? session.ClientName : session.Transport + " " + session.RemoteAddress;
}
