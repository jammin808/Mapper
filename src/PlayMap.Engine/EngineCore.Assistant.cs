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
                w.WriteString("line", redactor.Redact(l.Line));
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
        return CommandReply.Success(payload, $"approved {decided.Id} ({redactor.Redact(decided.Line)}) — {ran}");
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
            return CommandReply.Success(JsonOut(w => AssistantService.WriteProposal(w, p, redactor)), $"rejected {p.Id} ({redactor.Redact(p.Line)}) — it did not run");
        }
        catch (InvalidOperationException ex)
        {
            return CommandReply.Fail(ex.Message);
        }
    }

    private static string Who(SessionInfo session) =>
        session.ClientName.Length > 0 ? session.ClientName : session.Transport + " " + session.RemoteAddress;
}
