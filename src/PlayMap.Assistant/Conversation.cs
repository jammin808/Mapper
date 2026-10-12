namespace PlayMap.Assistant;

/// <summary>Who said a message. Tool results ride in a user message (one message for all of a turn's
/// results); a provider that wants them as their own role converts them on the way out.</summary>
public enum AssistantRole
{
    User,
    Assistant,
}

/// <summary>One part of a message in the provider-neutral history the session keeps. The history is
/// append-only: a part is never edited after it is sent, because a model's thinking is bound to the
/// conversation that produced it.</summary>
public interface IMessagePart
{
    /// <summary>"text", "thinking", "redacted_thinking", "tool_call" or "tool_result".</summary>
    string Kind { get; }
}

public sealed record TextPart(string Text) : IMessagePart
{
    public string Kind => "text";
}

/// <summary>A model's thinking, echoed back unchanged with its signature when the conversation continues.</summary>
public sealed record ThinkingPart(string Thinking, string Signature) : IMessagePart
{
    public string Kind => "thinking";
}

/// <summary>Thinking the service returned encrypted; echoed back unchanged.</summary>
public sealed record RedactedThinkingPart(string Data) : IMessagePart
{
    public string Kind => "redacted_thinking";
}

/// <summary>A tool call: its id, the tool's name and the arguments as the model wrote them (a JSON object;
/// read with JSON APIs, never matched as a string).</summary>
public sealed record ToolCallPart(string Id, string Name, string ArgumentsJson) : IMessagePart
{
    public string Kind => "tool_call";
}

/// <summary>A tool's answer to one call.</summary>
public sealed record ToolResultPart(string ToolCallId, string Content, bool IsError) : IMessagePart
{
    public string Kind => "tool_result";
}

public sealed record AssistantMessage(AssistantRole Role, IReadOnlyList<IMessagePart> Parts)
{
    public static AssistantMessage User(params IMessagePart[] parts) => new(AssistantRole.User, parts);

    public IEnumerable<ToolCallPart> ToolCalls => Parts.OfType<ToolCallPart>();

    public string Text => string.Concat(Parts.OfType<TextPart>().Select(p => p.Text));
}

/// <summary>A tool as the model sees it: a name, what it is for, and its arguments as a JSON Schema object
/// (type object, properties, required; no other properties allowed).</summary>
public sealed record ToolSpec(string Name, string Description, string InputSchemaJson);

/// <summary>One call to a model: the static system prompt (byte-stable, so it caches), the conversation so far
/// and the tools.</summary>
public sealed record AssistantRequest(string System, IReadOnlyList<AssistantMessage> Messages, IReadOnlyList<ToolSpec> Tools);

/// <summary>Why a model stopped.</summary>
public enum StopKind
{
    /// <summary>It said what it had to say.</summary>
    EndTurn,
    /// <summary>It wants its tool calls answered.</summary>
    ToolUse,
    /// <summary>It ran out of room for its answer.</summary>
    MaxTokens,
    /// <summary>It declined the request.</summary>
    Refusal,
    /// <summary>The conversation no longer fits the model's context.</summary>
    ContextFull,
    Other,
}

/// <summary>Token counts for one model call (or a sum of calls). Cache reads and writes say whether the static
/// prefix is being reused.</summary>
public readonly record struct UsageCounts(long Input, long Output, long CacheRead, long CacheWrite)
{
    public static UsageCounts operator +(UsageCounts a, UsageCounts b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite);
}

/// <summary>A model's answer: the assistant message's parts to append to the history as they are (the
/// provider has already dropped what must not be echoed), why it stopped, and on a refusal the category and
/// the service's explanation.</summary>
public sealed record AssistantReply(
    IReadOnlyList<IMessagePart> Parts,
    StopKind Stop,
    UsageCounts Usage,
    string ServedBy,
    string? RefusalCategory = null,
    string? RefusalExplanation = null,
    bool FellBack = false);

/// <summary>A language model behind one seam: Claude through the official SDK, or a local model on an
/// OpenAI-compatible endpoint. Tests use a scripted one. A provider never runs a tool: the session does,
/// through the risk gate.</summary>
public interface IAssistantProvider : IDisposable
{
    AssistantProviderKind Kind { get; }

    /// <summary>The model asked for, for the journal and the desk.</summary>
    string Model { get; }

    /// <summary>How many times in a row the model may be sent back a refused tool call to try again before
    /// the session gives up (a local model gets one retry).</summary>
    int ValidationRetries { get; }

    /// <summary>One model call. Text arrives through <paramref name="onText"/> as it is written (all at once
    /// when the provider does not stream). Failures throw <see cref="AssistantProviderException"/>.</summary>
    Task<AssistantReply> CompleteAsync(AssistantRequest request, Action<string>? onText, CancellationToken ct);
}

public enum ProviderFailure
{
    /// <summary>The key was refused or is missing.</summary>
    Auth,
    /// <summary>Too many requests: wait and ask again.</summary>
    RateLimited,
    /// <summary>The service or the local server is having trouble.</summary>
    Unavailable,
    /// <summary>No connection.</summary>
    Network,
    /// <summary>The model or the endpoint does not exist.</summary>
    NotFound,
    /// <summary>The request was refused as malformed (a bug here, not the operator's).</summary>
    BadRequest,
    Timeout,
    Other,
}

/// <summary>A model call that failed, in words for the desk. The message never carries a key.</summary>
public sealed class AssistantProviderException : Exception
{
    public AssistantProviderException(ProviderFailure failure, string message, Exception? inner = null)
        : base(message, inner) => Failure = failure;

    public AssistantProviderException()
        : this(ProviderFailure.Other, "The assistant failed.")
    {
    }

    public AssistantProviderException(string message)
        : this(ProviderFailure.Other, message)
    {
    }

    public AssistantProviderException(string message, Exception inner)
        : this(ProviderFailure.Other, message, inner)
    {
    }

    public ProviderFailure Failure { get; }

    /// <summary>Whether asking again later can succeed.</summary>
    public bool Retryable => Failure is ProviderFailure.RateLimited or ProviderFailure.Unavailable or ProviderFailure.Network or ProviderFailure.Timeout;
}
