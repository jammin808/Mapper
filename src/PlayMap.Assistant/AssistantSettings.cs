using PlayMap.Core.Wire;

namespace PlayMap.Assistant;

/// <summary>Which language model answers: none (the app works fully without one), Claude through the official
/// SDK, or a local model behind an OpenAI-compatible endpoint (Ollama, llama.cpp's server, LM Studio).</summary>
public enum AssistantProviderKind
{
    None,
    Claude,
    OpenAiCompatible,
}

/// <summary>How hard the model thinks before it answers: more is slower and costs more.</summary>
public enum AssistantEffort
{
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

/// <summary>The assistant's settings. Machine-local like the node's own settings, and free of secrets: the key
/// is the node's secret store's to hold and is handed to <see cref="AssistantProviders.Create"/> alone, so these
/// settings can be shown, journalled and saved anywhere.
/// <para>The gate: a low-risk verb runs on the assistant's word when <see cref="RunLowWithoutAsking"/> is on, a
/// review verb when <see cref="RunReviewWithoutAsking"/> is on; a high-risk verb always waits for a person and
/// no setting can change that.</para></summary>
public sealed record AssistantSettings
{
    public const string DefaultClaudeModel = "claude-opus-5-5";

    /// <summary>Ollama's OpenAI-compatible endpoint on this machine (llama.cpp's server is :8080/v1, LM Studio's
    /// :1234/v1).</summary>
    public const string DefaultLocalBaseUrl = "http://localhost:11434/v1";

    public const int MinToolIterations = 1;
    public const int MaxToolIterationsCeiling = 50;
    public const int MinMaxTokens = 1024;
    public const int MaxMaxTokens = 128_000;

    /// <summary>Off by default: an assistant is something the operator turns on.</summary>
    public bool Enabled { get; init; }

    public AssistantProviderKind Provider { get; init; } = AssistantProviderKind.Claude;

    /// <summary>The model's name as its provider knows it ("" = the provider's default).</summary>
    public string Model { get; init; } = DefaultClaudeModel;

    public AssistantEffort Effort { get; init; } = AssistantEffort.Medium;

    /// <summary>The endpoint: required for a local model; "" for Claude means the SDK's own.</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>Low-risk verbs (make a playlist, add an item, a new generator) run on the assistant's word.</summary>
    public bool RunLowWithoutAsking { get; init; } = true;

    /// <summary>Review verbs (SET, REMOVE, BLEND SOLVE…) run on the assistant's word. Off: a person looks first.</summary>
    public bool RunReviewWithoutAsking { get; init; }

    /// <summary>Model calls one request may make before the assistant stops and says so.</summary>
    public int MaxToolIterations { get; init; } = 12;

    public int MaxTokens { get; init; } = 16_000;

    /// <summary>When the model declines on policy grounds, the service re-serves the request on its recommended
    /// fallback model inside the same call (Claude only).</summary>
    public bool RefusalFallback { get; init; } = true;

    /// <summary>How long one model call may take (a local model on a small GPU is slow).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public static readonly AssistantSettings Default = new();

    /// <summary>A local model on an OpenAI-compatible endpoint.</summary>
    public static AssistantSettings Local(string model, string baseUrl = DefaultLocalBaseUrl) => new()
    {
        Enabled = true,
        Provider = AssistantProviderKind.OpenAiCompatible,
        Model = model,
        BaseUrl = baseUrl,
    };

    /// <summary>Whether a verb of this risk runs without a person. High is never in that set.</summary>
    public bool RunsWithoutAsking(VerbRisk risk) => risk switch
    {
        VerbRisk.Low => RunLowWithoutAsking,
        VerbRisk.Review => RunReviewWithoutAsking,
        _ => false,
    };

    /// <summary>The settings with every number in range and a model named.</summary>
    public AssistantSettings Normalised() => this with
    {
        Model = string.IsNullOrWhiteSpace(Model)
            ? Provider == AssistantProviderKind.Claude ? DefaultClaudeModel : ""
            : Model.Trim(),
        BaseUrl = (BaseUrl ?? "").Trim(),
        MaxToolIterations = Math.Clamp(MaxToolIterations, MinToolIterations, MaxToolIterationsCeiling),
        MaxTokens = Math.Clamp(MaxTokens, MinMaxTokens, MaxMaxTokens),
        RequestTimeout = RequestTimeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : RequestTimeout,
    };
}
