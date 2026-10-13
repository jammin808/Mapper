using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using PlayMap.Core.Bus;
using PlayMap.Core.Wire;

namespace PlayMap.Assistant;

/// <summary>How a request ended.</summary>
public enum TurnOutcome
{
    /// <summary>The model said what it had to say.</summary>
    Answered,
    /// <summary>The model declined; <see cref="AssistantTurnResult.Text"/> says why in its words.</summary>
    Refused,
    /// <summary>The answer ran out of room.</summary>
    MaxTokens,
    /// <summary>The request used its model calls without finishing.</summary>
    BudgetSpent,
    /// <summary>The model kept proposing calls the gate refused, past its retries.</summary>
    InvalidCalls,
    /// <summary>The conversation no longer fits the model.</summary>
    ContextFull,
    Cancelled,
    /// <summary>The model could not be reached or failed; the words say why.</summary>
    Failed,
    /// <summary>Nothing was sent: the assistant is off, busy, or there was no question.</summary>
    NotSent,
}

/// <summary>What one request came to: the outcome, the model's words, every line that ran or queued with the
/// node's own replies, the proposals waiting for a person, the tokens used and a status line for the desk.</summary>
public sealed record AssistantTurnResult(
    TurnOutcome Outcome,
    string Text,
    IReadOnlyList<LineOutcome> Lines,
    UsageCounts Usage,
    int ModelCalls,
    string Status)
{
    public IReadOnlyList<string> Queued => Lines.Where(l => l.Status == LineStatus.Queued && l.ProposalId is not null).Select(l => l.ProposalId!).ToList();
}

/// <summary>What the desk sees while a request runs.</summary>
public interface IAssistantEvent
{
    /// <summary>"text", "tool_call", "tool_result", "model_call" or "done".</summary>
    string Kind { get; }
}

/// <summary>Words as the model writes them.</summary>
public sealed record TextDelta(string Text) : IAssistantEvent
{
    public string Kind => "text";
}

public sealed record ToolCalled(string Tool, string ArgumentsJson) : IAssistantEvent
{
    public string Kind => "tool_call";
}

public sealed record ToolAnswered(string Tool, string Content, bool IsError, IReadOnlyList<LineOutcome> Lines) : IAssistantEvent
{
    public string Kind => "tool_result";
}

/// <summary>A model call finished: who served it, why it stopped, what it cost.</summary>
public sealed record ModelCalled(string ServedBy, StopKind Stop, UsageCounts Usage, bool FellBack) : IAssistantEvent
{
    public string Kind => "model_call";
}

public sealed record TurnDone(AssistantTurnResult Result) : IAssistantEvent
{
    public string Kind => "done";
}

/// <summary>One row of the conversation for the journal: the operator's words, the assistant's words, a tool
/// call and its answer, a note. Thinking is not in it, and secrets are masked.</summary>
public sealed record TranscriptEntry(long AtUnixMs, string Kind, string Text);

/// <summary>How a session presents itself and what it masks.</summary>
public sealed record AssistantSessionOptions
{
    /// <summary>Null makes one ("as" and eight hex digits).</summary>
    public string? SessionId { get; init; }

    /// <summary>The name the node's journal shows beside what the assistant ran.</summary>
    public string ClientName { get; init; } = "Assistant";

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The node's own secrets (pairing token, link keys, the assistant's key), masked wherever they
    /// would otherwise appear in what is sent, logged or kept.</summary>
    public IReadOnlyList<string> Secrets { get; init; } = [];
}

/// <summary>
/// One conversation with the assistant. Each request appends the operator's words (after a snapshot of the
/// node now) and runs the loop: the model answers; its tool calls run through the toolbox — reads freely,
/// changes only through propose_commands and the risk gate — and every result of one turn goes back in one
/// message; until the model ends its turn, declines, runs out of room or spends the request's budget.
/// The history is append-only: nothing sent is ever edited, because thinking is bound to the conversation.
/// One request at a time; cancellation leaves the history whole (a cancelled tool call is answered "not run").
/// </summary>
public sealed class AssistantSession
{
    /// <summary>The most recent proposals each snapshot lists (older ones are in the approvals list).</summary>
    public const int MaxProposalsInSnapshot = 20;

    private static readonly JsonSerializerOptions TranscriptJsonOptions = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IAssistantProvider _provider;
    private readonly IWireEndpoint _endpoint;
    private readonly ProposalQueue _approvals;
    private readonly AssistantSettings _settings;
    private readonly TimeProvider _time;
    private readonly SecretRedactor _redactor;
    private readonly AssistantToolbox _toolbox;
    private readonly Lock _gate = new();
    private readonly List<AssistantMessage> _history = [];
    private readonly List<TranscriptEntry> _transcript = [];
    private int _busy;

    public AssistantSession(IAssistantProvider provider, IWireEndpoint endpoint, ProposalQueue approvals, AssistantSettings settings, AssistantSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(settings);
        var o = options ?? new AssistantSessionOptions();
        _provider = provider;
        _endpoint = endpoint;
        _approvals = approvals;
        _settings = settings.Normalised();
        _time = o.Time;
        _redactor = new SecretRedactor(o.Secrets);
        Id = string.IsNullOrWhiteSpace(o.SessionId) ? "as" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)) : o.SessionId.Trim();
        Session = new SessionInfo("assistant:" + Id, "assistant", "127.0.0.1", true, o.ClientName, true);
        _toolbox = new AssistantToolbox(endpoint, approvals, _settings, Session, $"assistant ({provider.Model})", _time);
    }

    public string Id { get; }

    /// <summary>Who the node's command path sees when this session's lines run.</summary>
    public SessionInfo Session { get; }

    public AssistantSettings Settings => _settings;

    public bool Busy => Volatile.Read(ref _busy) != 0;

    /// <summary>Raised (on the request's thread) for every step of a request.</summary>
    public event Action<IAssistantEvent>? Activity;

    /// <summary>Journal lines: what was asked, what each model call cost (cache reads and writes included),
    /// what ran and what queued. No secrets.</summary>
    public event Action<string>? Logged;

    /// <summary>The conversation as the model has it, oldest first (a snapshot).</summary>
    internal IReadOnlyList<AssistantMessage> History
    {
        get
        {
            lock (_gate)
                return [.. _history];
        }
    }

    /// <summary>The conversation for the journal, oldest first (a snapshot).</summary>
    public IReadOnlyList<TranscriptEntry> Transcript
    {
        get
        {
            lock (_gate)
                return [.. _transcript];
        }
    }

    /// <summary>The transcript as JSON, for the journal to keep.</summary>
    public string TranscriptJson() => JsonSerializer.Serialize(Transcript, TranscriptJsonOptions);

    /// <summary>The proposals this session made, oldest first.</summary>
    public IReadOnlyList<Proposal> Proposals => _approvals.ForSession(Session.SessionId);

    /// <summary>One request: the operator's words in, the loop run to its end. Never throws for a failure of
    /// the model, the network or a tool: the result says what happened.</summary>
    public Task<AssistantTurnResult> AskAsync(string text, CancellationToken ct = default) => AskCoreAsync(text, null, ct);

    /// <summary>The same request as a stream of events, ending with <see cref="TurnDone"/>.</summary>
    public async IAsyncEnumerable<IAssistantEvent> AskStreamAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<IAssistantEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var run = PumpAsync(text, channel.Writer, ct);
        await foreach (var e in channel.Reader.ReadAllAsync(ct))
            yield return e;
        await run;
    }

    private async Task PumpAsync(string text, ChannelWriter<IAssistantEvent> writer, CancellationToken ct)
    {
        try
        {
            await AskCoreAsync(text, e => _ = writer.TryWrite(e), ct);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task<AssistantTurnResult> AskCoreAsync(string? text, Action<IAssistantEvent>? sink, CancellationToken ct)
    {
        var question = _redactor.Redact((text ?? "").Trim());
        if (question.Length == 0)
            return Done(sink, NotSent("Type a question, or what you want done."));
        if (!_settings.Enabled || _settings.Provider == AssistantProviderKind.None)
            return Done(sink, NotSent("The assistant is off. The desk's Explain works without it."));
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return Done(sink, NotSent("Still working on the last request."));
        try
        {
            return Done(sink, await RunAsync(question, sink, ct));
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private async Task<AssistantTurnResult> RunAsync(string question, Action<IAssistantEvent>? sink, CancellationToken ct)
    {
        Note("operator", question);
        Log($"assistant: asked ({question.Length.ToString(CultureInfo.InvariantCulture)} characters)");
        var now = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone);
        var snapshot = AssistantPrompt.Snapshot(_endpoint.State().Json, _endpoint.Context(), Proposals.TakeLast(MaxProposalsInSnapshot).ToList(), now);
        Append(AssistantMessage.User(new TextPart(snapshot), new TextPart(question)));

        var words = new StringBuilder();
        var lines = new List<LineOutcome>();
        var usage = default(UsageCounts);
        var invalidRounds = 0;
        for (var call = 1; ; call++)
        {
            if (call > _settings.MaxToolIterations)
                return Result(TurnOutcome.BudgetSpent, words, lines, usage, call - 1,
                    string.Create(CultureInfo.InvariantCulture, $"Stopped after {_settings.MaxToolIterations} steps without finishing — say \"go on\" to continue."));
            AssistantReply reply;
            var started = _time.GetTimestamp();
            try
            {
                var request = new AssistantRequest(AssistantPrompt.System, History, AssistantTools.Specs);
                reply = await _provider.CompleteAsync(request, t => Emit(sink, new TextDelta(t)), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Result(TurnOutcome.Cancelled, words, lines, usage, call - 1, "Cancelled.");
            }
            catch (AssistantProviderException ex)
            {
                Log("assistant: the model call failed — " + _redactor.Redact(ex.Message));
                return Result(TurnOutcome.Failed, words, lines, usage, call - 1, _redactor.Redact(ex.Message));
            }
            catch (Exception ex)
            {
                Log("assistant: the model call failed — " + _redactor.Redact(ex.Message));
                return Result(TurnOutcome.Failed, words, lines, usage, call - 1, "The assistant failed: " + _redactor.Redact(ex.Message));
            }
            usage += reply.Usage;
            LogCall(reply, _time.GetElapsedTime(started));
            Emit(sink, new ModelCalled(reply.ServedBy, reply.Stop, reply.Usage, reply.FellBack));
            if (reply.Parts.Count > 0)
                Append(new AssistantMessage(AssistantRole.Assistant, reply.Parts));
            var said = string.Concat(reply.Parts.OfType<TextPart>().Select(p => p.Text)).Trim();
            if (said.Length > 0)
            {
                Note("assistant", said);
                words.Append(words.Length > 0 ? "\n\n" : "").Append(said);
            }

            var calls = reply.Parts.OfType<ToolCallPart>().ToList();
            if (calls.Count > 0 && reply.Stop is StopKind.MaxTokens or StopKind.Refusal or StopKind.ContextFull)
            {
                // A call in an answer that was cut off or declined is not run, but it is answered: every call in
                // the history has its result, or the next request is refused.
                Append(new AssistantMessage(AssistantRole.User,
                    calls.Select(c => (IMessagePart)new ToolResultPart(c.Id, "Not run: the answer stopped before this call could be trusted.", true)).ToList()));
            }

            switch (reply.Stop)
            {
                case StopKind.ToolUse or StopKind.EndTurn or StopKind.Other when calls.Count > 0:
                    var round = await RunToolsAsync(calls, sink, ct);
                    lines.AddRange(round.Lines);
                    if (round.Cancelled)
                        return Result(TurnOutcome.Cancelled, words, lines, usage, call, "Cancelled.");
                    invalidRounds = round.Invalid ? invalidRounds + 1 : 0;
                    if (invalidRounds > _provider.ValidationRetries)
                        return Result(TurnOutcome.InvalidCalls, words, lines, usage, call,
                            "The model did not form a valid command, so nothing it proposed ran. Try saying it another way" +
                            (_provider.Kind == AssistantProviderKind.OpenAiCompatible ? ", or use a larger local model." : "."));
                    continue;
                case StopKind.ToolUse:
                case StopKind.EndTurn:
                    return Result(TurnOutcome.Answered, words, lines, usage, call, "");
                case StopKind.MaxTokens:
                    return Result(TurnOutcome.MaxTokens, words, lines, usage, call, "The answer ran out of room — ask for less at once.");
                case StopKind.Refusal:
                    var category = string.IsNullOrWhiteSpace(reply.RefusalCategory) ? "" : $" (category: {reply.RefusalCategory})";
                    var why = string.IsNullOrWhiteSpace(reply.RefusalExplanation) ? "" : " " + reply.RefusalExplanation.Trim();
                    var refusal = $"The model declined this request{category}.{why} Nothing in the declined answer ran; try asking another way.";
                    Note("note", refusal);
                    return Result(TurnOutcome.Refused, words, lines, usage, call, refusal);
                case StopKind.ContextFull:
                    return Result(TurnOutcome.ContextFull, words, lines, usage, call, "This conversation is too long for the model — start a new one.");
                default:
                    return Result(TurnOutcome.Answered, words, lines, usage, call, "");
            }
        }
    }

    /// <summary>Runs one turn's tool calls in order and appends every result in one user message. A call not
    /// reached because of a cancel is answered "not run", so the history stays whole.</summary>
    private async Task<(List<LineOutcome> Lines, bool Invalid, bool Cancelled)> RunToolsAsync(List<ToolCallPart> calls, Action<IAssistantEvent>? sink, CancellationToken ct)
    {
        var results = new List<IMessagePart>(calls.Count);
        var lines = new List<LineOutcome>();
        var invalid = false;
        var cancelled = false;
        foreach (var c in calls)
        {
            if (cancelled || ct.IsCancellationRequested)
            {
                cancelled = true;
                results.Add(new ToolResultPart(c.Id, "Not run: the operator cancelled the request.", true));
                continue;
            }
            var args = _redactor.Redact(c.ArgumentsJson);
            Emit(sink, new ToolCalled(c.Name, args));
            Note("tool-call", c.Name + " " + args);
            ToolOutcome outcome;
            try
            {
                outcome = await _toolbox.RunAsync(c, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome = ToolOutcome.Error("Not finished: the operator cancelled the request.") with { Cancelled = true };
            }
            catch (Exception ex)
            {
                outcome = ToolOutcome.Error("The tool failed: " + ex.Message);
            }
            if (outcome.Cancelled)
                cancelled = true;
            if (outcome.Invalid)
                invalid = true;
            lines.AddRange(outcome.Lines);
            foreach (var l in outcome.Lines)
                Log($"assistant: {l.StatusWord} {VerbCatalog.Redacted(l.Line)} ({ShowContext.RiskWord(l.Risk)}) — {l.Reply}");
            results.Add(new ToolResultPart(c.Id, outcome.Content, outcome.IsError));
            Emit(sink, new ToolAnswered(c.Name, outcome.Content, outcome.IsError, outcome.Lines));
            Note("tool-result", _redactor.Redact(outcome.Content));
        }
        Append(new AssistantMessage(AssistantRole.User, results));
        return (lines, invalid, cancelled);
    }

    private AssistantTurnResult Result(TurnOutcome outcome, StringBuilder words, List<LineOutcome> lines, UsageCounts usage, int calls, string note)
    {
        var parts = new List<string>();
        if (note.Length > 0)
            parts.Add(note);
        var ran = lines.Count(l => l.Status == LineStatus.Ran);
        var refused = lines.Count(l => l.Status == LineStatus.Refused);
        var unknown = lines.Count(l => l.Status == LineStatus.Unknown);
        var queued = lines.Where(l => l.Status == LineStatus.Queued).Select(l => l.ProposalId).ToList();
        if (ran > 0)
            parts.Add(ran == 1 ? "1 line ran." : string.Create(CultureInfo.InvariantCulture, $"{ran} lines ran."));
        if (refused > 0)
            parts.Add(refused == 1 ? "1 line was refused by the node." : string.Create(CultureInfo.InvariantCulture, $"{refused} lines were refused by the node."));
        if (unknown > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{unknown} gave no answer — check STATE."));
        if (queued.Count > 0)
            parts.Add((queued.Count == 1 ? "1 waits" : string.Create(CultureInfo.InvariantCulture, $"{queued.Count} wait")) + $" for your approval ({string.Join(", ", queued)}).");
        var text = words.ToString();
        if (text.Length == 0 && outcome is not TurnOutcome.Answered && note.Length > 0)
            text = note;
        return new AssistantTurnResult(outcome, text, lines, usage, calls, string.Join(" ", parts));
    }

    private static AssistantTurnResult NotSent(string why) => new(TurnOutcome.NotSent, why, [], default, 0, why);

    private AssistantTurnResult Done(Action<IAssistantEvent>? sink, AssistantTurnResult result)
    {
        if (result.Outcome != TurnOutcome.NotSent)
            Log("assistant: " + result.Outcome.ToString().ToLowerInvariant() + (result.Status.Length > 0 ? " — " + result.Status : ""));
        Emit(sink, new TurnDone(result));
        return result;
    }

    private void Append(AssistantMessage message)
    {
        lock (_gate)
            _history.Add(message);
    }

    private void Note(string kind, string text)
    {
        var entry = new TranscriptEntry(_time.GetUtcNow().ToUnixTimeMilliseconds(), kind, _redactor.Redact(text));
        lock (_gate)
            _transcript.Add(entry);
    }

    private void LogCall(AssistantReply reply, TimeSpan took)
    {
        var u = reply.Usage;
        Log(string.Create(CultureInfo.InvariantCulture,
            $"assistant: {reply.ServedBy} answered ({reply.Stop}) in {took.TotalMilliseconds:0} ms — input {u.Input} (cache read {u.CacheRead}, cache write {u.CacheWrite}), output {u.Output}"));
        if (reply.FellBack)
        {
            var note = $"The requested model declined; {reply.ServedBy} answered instead.";
            Note("note", note);
            Log("assistant: " + note);
        }
    }

    private void Log(string line)
    {
        try
        {
            Logged?.Invoke(_redactor.Redact(line));
        }
        catch (Exception)
        {
            // The journal is a listener: a listener that fails must not fail the request it is recording.
        }
    }

    private void Emit(Action<IAssistantEvent>? sink, IAssistantEvent e)
    {
        try
        {
            Activity?.Invoke(e);
            sink?.Invoke(e);
        }
        catch (Exception ex)
        {
            Log("assistant: a listener failed (" + ex.Message + ")");
        }
    }
}
