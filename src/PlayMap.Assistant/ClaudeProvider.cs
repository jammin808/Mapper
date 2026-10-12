using System.Globalization;
using System.Net;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using BetaRole = Anthropic.Models.Beta.Messages.Role;

namespace PlayMap.Assistant;

/// <summary>The seam between the provider and the SDK's client, so a request can be built and read in a test
/// with no network.</summary>
internal interface IClaudeTransport : IDisposable
{
    IAsyncEnumerable<BetaRawMessageStreamEvent> StreamAsync(MessageCreateParams parameters, CancellationToken ct);
}

/// <summary>The official SDK's client. The key comes from the node's secret store (or, when none is given,
/// the SDK's own credential sources); it is never logged.</summary>
internal sealed class SdkClaudeTransport : IClaudeTransport
{
    private readonly AnthropicClient _client;

    public SdkClaudeTransport(string? apiKey, string baseUrl, TimeSpan timeout)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var url = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim();
        // The client's options are init-only: one initializer per combination of what is given.
        _client = (key, url) switch
        {
            (null, null) => new AnthropicClient { Timeout = timeout },
            (null, { } u) => new AnthropicClient { BaseUrl = u, Timeout = timeout },
            ({ } k, null) => new AnthropicClient { ApiKey = k, Timeout = timeout },
            ({ } k, { } u) => new AnthropicClient { ApiKey = k, BaseUrl = u, Timeout = timeout },
        };
    }

    public IAsyncEnumerable<BetaRawMessageStreamEvent> StreamAsync(MessageCreateParams parameters, CancellationToken ct) =>
        _client.Beta.Messages.CreateStreaming(parameters, ct);

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Claude through the official SDK, on the beta messages path so a policy decline is re-served server-side by
/// the service's recommended fallback model inside the same call (fallbacks "default"). Every call streams;
/// text deltas go to the desk as they arrive and the whole message is assembled by the SDK's aggregator.
/// The static system prompt carries the cache breakpoint (tools and system are the cached prefix) and the
/// request asks for automatic caching of the growing conversation after it. Thinking is left to the model
/// (never configured); effort is set explicitly. Tool choice stays automatic: the prompt steers, strict tool
/// schemas hold the arguments to their shape, and the session checks every call again anyway.
/// </summary>
public sealed class ClaudeProvider : IAssistantProvider
{
    private readonly AssistantSettings _settings;
    private readonly IClaudeTransport _transport;

    /// <param name="apiKey">From the node's secret store; null or blank lets the SDK find its own credentials.</param>
    public ClaudeProvider(AssistantSettings settings, string? apiKey)
        : this(settings, CreateTransport(settings, apiKey))
    {
    }

    internal ClaudeProvider(AssistantSettings settings, IClaudeTransport transport)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Normalised();
        _transport = transport;
    }

    public AssistantProviderKind Kind => AssistantProviderKind.Claude;

    public string Model => _settings.Model;

    public int ValidationRetries => 2;

    public async Task<AssistantReply> CompleteAsync(AssistantRequest request, Action<string>? onText, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = BuildParams(request, _settings);
        var aggregator = new BetaMessageContentAggregator();
        try
        {
            await foreach (var e in _transport.StreamAsync(parameters, ct).CollectAsync(aggregator).WithCancellation(ct))
            {
                if (onText is not null && e.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text) && text.Text.Length > 0)
                    onText(text.Text);
            }
            return ReadMessage(aggregator.Message(), _settings.Model);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new AssistantProviderException(ProviderFailure.Auth, "The service refused the assistant's key (401) — check the key in the assistant's settings.", ex);
        }
        catch (AnthropicForbiddenException ex)
        {
            throw new AssistantProviderException(ProviderFailure.Auth, $"This key may not use {_settings.Model} (403).", ex);
        }
        catch (AnthropicNotFoundException ex)
        {
            throw new AssistantProviderException(ProviderFailure.NotFound, $"The service does not know the model '{_settings.Model}' (404) — check the assistant's model.", ex);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new AssistantProviderException(ProviderFailure.RateLimited, "Too many requests (429) — wait a moment and ask again.", ex);
        }
        catch (AnthropicBadRequestException ex)
        {
            throw new AssistantProviderException(ProviderFailure.BadRequest, "The service refused the request (400): " + Short(ex.Message), ex);
        }
        catch (Anthropic4xxException ex)
        {
            throw new AssistantProviderException(ProviderFailure.Other, $"The service refused the request ({(int)ex.StatusCode}): " + Short(ex.Message), ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new AssistantProviderException(ProviderFailure.Unavailable, "The service is having trouble — try again in a minute.", ex);
        }
        catch (AnthropicApiException ex) when (ex.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
        {
            throw new AssistantProviderException(ProviderFailure.Timeout, "The service took too long — try again.", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new AssistantProviderException(ProviderFailure.Other, string.Create(CultureInfo.InvariantCulture, $"The service answered {(int)ex.StatusCode}: ") + Short(ex.Message), ex);
        }
        catch (AnthropicSseException ex)
        {
            throw new AssistantProviderException(ProviderFailure.Unavailable, "The answer's stream broke off — ask again.", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new AssistantProviderException(ProviderFailure.Network, "No connection to the service — the cloud assistant needs the internet (a local model does not).", ex);
        }
        catch (AnthropicException ex)
        {
            throw new AssistantProviderException(ProviderFailure.Other, "The assistant failed: " + Short(ex.Message), ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AssistantProviderException(ProviderFailure.Timeout, "The service took too long to answer — try again.", ex);
        }
    }

    public void Dispose() => _transport.Dispose();

    /// <summary>The request as it leaves: the model, the cached static system block, the tools (strict, closed
    /// schemas), automatic caching for the conversation, explicit effort, and — unless turned off — the
    /// server-side refusal fallback in its "default" form. No thinking configuration, no sampling parameters,
    /// no forced tool choice, no prefill.</summary>
    public static MessageCreateParams BuildParams(AssistantRequest request, AssistantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings.Normalised();
        var parameters = new MessageCreateParams
        {
            Model = s.Model,
            MaxTokens = s.MaxTokens,
            System = new List<BetaTextBlockParam> { new() { Text = request.System, CacheControl = new BetaCacheControlEphemeral() } },
            Messages = request.Messages.Select(ToParam).ToList(),
            Tools = request.Tools.Select(t => (BetaToolUnion)ToTool(t)).ToList(),
            OutputConfig = new BetaOutputConfig { Effort = ToEffort(s.Effort) },
            CacheControl = new BetaCacheControlEphemeral(),
        };
        return s.RefusalFallback
            ? parameters with { Betas = [AnthropicBeta.ServerSideFallback2026_07_01], Fallbacks = new Default() }
            : parameters;
    }

    /// <summary>A response as the session's history keeps it. After a fallback boundary (the last fallback
    /// block), only text from before it is echoed — the declined model's thinking and tool calls are dropped,
    /// so they never run — and everything after it is echoed whole. Thinking echoes with its signature.</summary>
    internal static AssistantReply ReadMessage(BetaMessage message, string requestedModel)
    {
        var blocks = message.Content ?? [];
        var boundary = -1;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].TryPickFallback(out _))
                boundary = i;
        }
        var parts = new List<IMessagePart>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.TryPickText(out var text))
            {
                if (!string.IsNullOrEmpty(text.Text))
                    parts.Add(new TextPart(text.Text));
                continue;
            }
            if (i < boundary)
                continue;
            if (b.TryPickThinking(out var thinking))
                parts.Add(new ThinkingPart(thinking.Thinking ?? "", thinking.Signature ?? ""));
            else if (b.TryPickRedactedThinking(out var redacted))
                parts.Add(new RedactedThinkingPart(redacted.Data));
            else if (b.TryPickToolUse(out var toolUse))
                parts.Add(new ToolCallPart(toolUse.ID, toolUse.Name, JsonSerializer.Serialize(toolUse.Input)));
            // A fallback marker and any block type this provider does not model are not echoed.
        }
        var stop = message.StopReason is { } sr ? sr.Raw() : "";
        var kind = stop switch
        {
            "end_turn" or "stop_sequence" => StopKind.EndTurn,
            "tool_use" => StopKind.ToolUse,
            "max_tokens" => StopKind.MaxTokens,
            "refusal" => StopKind.Refusal,
            "model_context_window_exceeded" => StopKind.ContextFull,
            _ => StopKind.Other,
        };
        var usage = message.Usage is { } u
            ? new UsageCounts(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0)
            : default;
        var served = message.Model is { } m ? m.Raw() : requestedModel;
        var fellBack = boundary >= 0 || !string.Equals(served, requestedModel, StringComparison.Ordinal);
        string? category = null;
        string? explanation = null;
        if (kind == StopKind.Refusal && message.StopDetails is { } details)
        {
            category = details.Category is { } c ? c.Raw() : null;
            explanation = details.Explanation;
        }
        return new AssistantReply(parts, kind, usage, served, category, explanation, fellBack);
    }

    private static IClaudeTransport CreateTransport(AssistantSettings settings, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings.Normalised();
        return new SdkClaudeTransport(apiKey, s.BaseUrl, s.RequestTimeout);
    }

    private static BetaMessageParam ToParam(AssistantMessage m) => new()
    {
        Role = m.Role == AssistantRole.User ? BetaRole.User : BetaRole.Assistant,
        Content = m.Parts.Select(ToBlock).OfType<BetaContentBlockParam>().ToList(),
    };

    private static BetaContentBlockParam? ToBlock(IMessagePart part) => part switch
    {
        // The service refuses an empty text block; a part with no words carries nothing to echo.
        TextPart { Text.Length: > 0 } t => new BetaTextBlockParam { Text = t.Text },
        ThinkingPart th => new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature },
        RedactedThinkingPart r => new BetaRedactedThinkingBlockParam { Data = r.Data },
        ToolCallPart c => new BetaToolUseBlockParam { ID = c.Id, Name = c.Name, Input = ParseObject(c.ArgumentsJson) },
        ToolResultPart r => new BetaToolResultBlockParam { ToolUseID = r.ToolCallId, Content = r.Content, IsError = r.IsError },
        _ => null,
    };

    private static BetaTool ToTool(ToolSpec t) => new()
    {
        Name = t.Name,
        Description = t.Description,
        InputSchema = new InputSchema(ParseObject(t.InputSchemaJson)),
        Strict = true,
    };

    private static Effort ToEffort(AssistantEffort e) => e switch
    {
        AssistantEffort.Low => Effort.Low,
        AssistantEffort.High => Effort.High,
        AssistantEffort.XHigh => Effort.Xhigh,
        AssistantEffort.Max => Effort.Max,
        _ => Effort.Medium,
    };

    /// <summary>A JSON object as the SDK's dictionary of elements (cloned, so they outlive the document).</summary>
    internal static Dictionary<string, JsonElement> ParseObject(string json)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return d;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                    d[p.Name] = p.Value.Clone();
            }
        }
        catch (JsonException)
        {
            // Arguments the model wrote that are not JSON were already answered as an error; they echo empty.
            d.Clear();
        }
        return d;
    }

    private static string Short(string message) => message.Length <= 300 ? message : message[..300] + "…";
}
