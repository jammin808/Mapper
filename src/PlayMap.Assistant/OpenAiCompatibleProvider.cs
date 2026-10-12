using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PlayMap.Assistant;

/// <summary>
/// A local model on an OpenAI-compatible endpoint (/v1/chat/completions — Ollama, llama.cpp's server, LM
/// Studio): tools in the "function" form, tool results as "tool" messages, one request per call with no
/// streaming (the whole text reaches the desk at once). Local models are weaker at tool use: a tool call
/// written as bare JSON in the text is read as a call, every call is checked by the session like any other,
/// and a refused call gets one retry with the reason before the session gives up. Nothing unchecked runs.
/// The optional key rides in the Authorization header only.
/// </summary>
public sealed class OpenAiCompatibleProvider : IAssistantProvider
{
    private readonly AssistantSettings _settings;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string? _apiKey;

    /// <param name="handler">A handler for tests; null makes the provider's own (revocation checked).</param>
    public OpenAiCompatibleProvider(AssistantSettings settings, string? apiKey, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Normalised();
        _endpoint = EndpointFor(_settings.BaseUrl)
            ?? throw new ArgumentException($"A local model needs its endpoint's base URL, http(s)://host:port/v1 (Ollama's is {AssistantSettings.DefaultLocalBaseUrl}).", nameof(settings));
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { CheckCertificateRevocationList = true }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = _settings.RequestTimeout;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public AssistantProviderKind Kind => AssistantProviderKind.OpenAiCompatible;

    public string Model => _settings.Model;

    public int ValidationRetries => 1;

    /// <summary>The chat-completions URL the provider posts to.</summary>
    public Uri Endpoint => _endpoint;

    public async Task<AssistantReply> CompleteAsync(AssistantRequest request, Action<string>? onText, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(BuildBody(request, _settings), Encoding.UTF8, "application/json"),
        };
        if (_apiKey is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new AssistantProviderException(ProviderFailure.Timeout,
                string.Create(CultureInfo.InvariantCulture, $"The local model did not answer within {_settings.RequestTimeout.TotalSeconds:0} s — a large model on a small GPU is slow: raise the timeout or use a smaller model."), ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AssistantProviderException(ProviderFailure.Network, $"No answer from the local model at {_endpoint.GetLeftPart(UriPartial.Authority)} — is its server running? ({ex.Message})", ex);
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw Failure(response.StatusCode, body);
            var reply = ParseResponse(body, request, _settings.Model);
            if (onText is not null)
            {
                foreach (var t in reply.Parts.OfType<TextPart>())
                    onText(t.Text);
            }
            return reply;
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The request body: the system prompt as the first message, the history in the chat form (tool
    /// results as "tool" messages, thinking left out — it belongs to another provider), the tools as functions,
    /// tool choice automatic, no streaming.</summary>
    internal static string BuildBody(AssistantRequest request, AssistantSettings settings)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", settings.Model);
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", request.System);
            w.WriteEndObject();
            foreach (var m in request.Messages)
                WriteMessage(w, m);
            w.WriteEndArray();
            w.WriteStartArray("tools");
            foreach (var t in request.Tools)
            {
                w.WriteStartObject();
                w.WriteString("type", "function");
                w.WriteStartObject("function");
                w.WriteString("name", t.Name);
                w.WriteString("description", t.Description);
                w.WritePropertyName("parameters");
                w.WriteRawValue(t.InputSchemaJson);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("tool_choice", "auto");
            w.WriteBoolean("stream", false);
            w.WriteNumber("max_tokens", settings.MaxTokens);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteMessage(Utf8JsonWriter w, AssistantMessage m)
    {
        var text = string.Join("\n\n", m.Parts.OfType<TextPart>().Select(p => p.Text).Where(t => t.Length > 0));
        if (m.Role == AssistantRole.User)
        {
            foreach (var r in m.Parts.OfType<ToolResultPart>())
            {
                w.WriteStartObject();
                w.WriteString("role", "tool");
                w.WriteString("tool_call_id", r.ToolCallId);
                w.WriteString("content", r.IsError ? "ERROR: " + r.Content : r.Content);
                w.WriteEndObject();
            }
            if (text.Length > 0)
            {
                w.WriteStartObject();
                w.WriteString("role", "user");
                w.WriteString("content", text);
                w.WriteEndObject();
            }
            return;
        }
        var calls = m.Parts.OfType<ToolCallPart>().ToList();
        if (text.Length == 0 && calls.Count == 0)
            return;
        w.WriteStartObject();
        w.WriteString("role", "assistant");
        if (text.Length > 0)
            w.WriteString("content", text);
        else
            w.WriteNull("content");
        if (calls.Count > 0)
        {
            w.WriteStartArray("tool_calls");
            foreach (var c in calls)
            {
                w.WriteStartObject();
                w.WriteString("id", c.Id);
                w.WriteString("type", "function");
                w.WriteStartObject("function");
                w.WriteString("name", c.Name);
                w.WriteString("arguments", string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    /// <summary>A chat-completions answer as the session's reply. A call without an id gets one that is unique
    /// in the conversation; a model's thinking in &lt;think&gt; tags is left out of its words; a tool call
    /// written as a bare JSON object in the text ({"name": a tool, "arguments"/"parameters": {…}}) is read as
    /// a call.</summary>
    internal static AssistantReply ParseResponse(string json, AssistantRequest request, string requestedModel)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new AssistantProviderException(ProviderFailure.Other, "The local model's answer is not JSON — is the base URL an OpenAI-compatible endpoint (…/v1)?", ex);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new AssistantProviderException(ProviderFailure.Other, "The local model's answer has no choices — is the base URL an OpenAI-compatible endpoint (…/v1)?");
            var choice = choices[0];
            var finish = Str(choice, "finish_reason") ?? "";
            var msg = choice.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
            var turn = request.Messages.Count.ToString(CultureInfo.InvariantCulture);
            var parts = new List<IMessagePart>();
            var calls = new List<ToolCallPart>();
            if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var fn = tc.ValueKind == JsonValueKind.Object && tc.TryGetProperty("function", out var f) ? f : default;
                    var name = fn.ValueKind == JsonValueKind.Object ? Str(fn, "name") ?? "" : "";
                    var id = Str(tc, "id") is { Length: > 0 } given ? given : $"call_{turn}_{i.ToString(CultureInfo.InvariantCulture)}";
                    calls.Add(new ToolCallPart(id, name, Arguments(fn)));
                    i++;
                }
            }
            var content = msg.ValueKind == JsonValueKind.Object ? StripThinking(Str(msg, "content") ?? "") : "";
            if (calls.Count == 0 && TryReadTextCall(content, request, $"call_{turn}_0") is { } textCall)
            {
                calls.Add(textCall);
                content = "";
            }
            if (content.Trim().Length > 0)
                parts.Add(new TextPart(content.Trim()));
            parts.AddRange(calls);
            var stop = calls.Count > 0 ? StopKind.ToolUse
                : finish switch
                {
                    "length" => StopKind.MaxTokens,
                    "content_filter" => StopKind.Refusal,
                    _ => StopKind.EndTurn,
                };
            var usage = default(UsageCounts);
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                var cached = u.TryGetProperty("prompt_tokens_details", out var d) && d.ValueKind == JsonValueKind.Object ? Long(d, "cached_tokens") : 0;
                usage = new UsageCounts(Long(u, "prompt_tokens"), Long(u, "completion_tokens"), cached, 0);
            }
            var served = Str(root, "model") is { Length: > 0 } model ? model : requestedModel;
            return new AssistantReply(parts, stop, usage, served,
                RefusalExplanation: stop == StopKind.Refusal ? "The local server's content filter stopped the answer." : null);
        }
    }

    private static AssistantProviderException Failure(HttpStatusCode status, string body)
    {
        var detail = ErrorDetail(body);
        var code = ((int)status).ToString(CultureInfo.InvariantCulture);
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new AssistantProviderException(ProviderFailure.Auth, $"The local endpoint refused the key ({code}){detail}."),
            HttpStatusCode.NotFound =>
                new AssistantProviderException(ProviderFailure.NotFound, $"The endpoint or the model is not there ({code}){detail} — check the base URL and that the model is installed (for Ollama: ollama pull <model>)."),
            HttpStatusCode.TooManyRequests =>
                new AssistantProviderException(ProviderFailure.RateLimited, $"The local server is busy ({code}){detail} — ask again in a moment."),
            >= HttpStatusCode.InternalServerError =>
                new AssistantProviderException(ProviderFailure.Unavailable, $"The local server failed ({code}){detail}."),
            HttpStatusCode.BadRequest =>
                new AssistantProviderException(ProviderFailure.BadRequest, $"The local server refused the request ({code}){detail} — the model may not support tools; choose one that does."),
            _ => new AssistantProviderException(ProviderFailure.Other, $"The local server answered {code}{detail}."),
        };
    }

    /// <summary>": the server's own words", short, or nothing.</summary>
    private static string ErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";
        var words = body.Trim();
        try
        {
            using var doc = JsonDocument.Parse(words);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var e))
                words = e.ValueKind == JsonValueKind.String ? e.GetString() ?? words
                    : e.ValueKind == JsonValueKind.Object && Str(e, "message") is { } message ? message : words;
        }
        catch (JsonException)
        {
            // A plain-text error body is shown as it is.
        }
        words = words.Replace('\n', ' ').Replace('\r', ' ');
        return ": " + (words.Length <= 200 ? words : words[..200] + "…");
    }

    /// <summary>The arguments as a JSON object's text: a string (the standard form) or an object (some servers).</summary>
    private static string Arguments(JsonElement fn)
    {
        if (fn.ValueKind != JsonValueKind.Object || !fn.TryGetProperty("arguments", out var a))
            return "{}";
        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() is { Length: > 0 } s ? s : "{}",
            JsonValueKind.Object => a.GetRawText(),
            _ => "{}",
        };
    }

    /// <summary>A tool call some local models write as text: a JSON object (in a code fence or not) naming one
    /// of the request's tools with its arguments. Null when the text is anything else.</summary>
    private static ToolCallPart? TryReadTextCall(string content, AssistantRequest request, string id)
    {
        var t = content.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = t.IndexOf('\n', StringComparison.Ordinal);
            var close = t.LastIndexOf("```", StringComparison.Ordinal);
            if (firstBreak < 0 || close <= firstBreak)
                return null;
            t = t[(firstBreak + 1)..close].Trim();
        }
        if (!t.StartsWith('{') || !t.EndsWith('}'))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(t);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Str(r, "name") is not { } name || !request.Tools.Any(x => x.Name == name))
                return null;
            var args = r.TryGetProperty("arguments", out var a) || r.TryGetProperty("parameters", out a)
                ? a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText()
                : "{}";
            return new ToolCallPart(id, name, args);
        }
        catch (JsonException)
        {
            // Text that only looks like JSON is the model's words, not a call.
            return null;
        }
    }

    /// <summary>The words without a reasoning model's &lt;think&gt;…&lt;/think&gt; sections.</summary>
    internal static string StripThinking(string content)
    {
        var s = content;
        while (true)
        {
            var open = s.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (open < 0)
                return s;
            var close = s.IndexOf("</think>", open, StringComparison.OrdinalIgnoreCase);
            s = close < 0 ? s[..open] : s[..open] + s[(close + "</think>".Length)..];
        }
    }

    /// <summary>The chat-completions URL for a base URL ("…/v1" or the full path); null when it is not http(s).</summary>
    internal static Uri? EndpointFor(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;
        var trimmed = baseUrl.Trim().TrimEnd('/');
        var url = trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/chat/completions";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : 0;
}
