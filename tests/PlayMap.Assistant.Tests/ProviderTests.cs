using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace PlayMap.Assistant.Tests;

/// <summary>Both providers without a network: the OpenAI-compatible client's wire form (tools as functions,
/// results as tool messages) and its reading of a canned answer; the Claude request as the SDK would send it
/// (model, cached system block, strict tools, effort, the refusal fallback) and the Claude answer as the
/// history keeps it (thinking echoed, a declined model's calls dropped after a fallback).</summary>
public class ProviderTests
{
    private static AssistantRequest SampleConversation() => new(
        AssistantPrompt.System,
        [
            AssistantMessage.User(new TextPart("=== THE NODE NOW ==="), new TextPart("start the show at 8pm")),
            new AssistantMessage(AssistantRole.Assistant,
                [new ThinkingPart("", "sig-1"), new TextPart("Checking the playlists."), new ToolCallPart("toolu_1", AssistantTools.GetState, "{}")]),
            AssistantMessage.User(new ToolResultPart("toolu_1", "{\"seq\":412}", false)),
        ],
        AssistantTools.Specs);

    // ---- The local model's wire ----

    private const string ToolCallAnswer =
        """
        {"id":"chatcmpl-1","object":"chat.completion","model":"qwen3:14b",
         "choices":[{"index":0,"message":{"role":"assistant","content":"<think>which windows?</think>",
           "tool_calls":[{"id":"call_abc","type":"function","function":{"name":"propose_commands","arguments":"{\"lines\":[\"PLAYLIST NEW Walk-in\"],\"reason\":\"a list\"}"}}]},
           "finish_reason":"tool_calls"}],
         "usage":{"prompt_tokens":1200,"completion_tokens":40,"prompt_tokens_details":{"cached_tokens":1000}}}
        """;

    [Fact]
    public async Task TheLocalClientSendsToolsAsFunctionsAndResultsAsToolMessages()
    {
        using var handler = new CannedHandler(ToolCallAnswer);
        using var provider = new OpenAiCompatibleProvider(AssistantSettings.Local("qwen3:14b", "http://127.0.0.1:11434/v1/"), "local-key", handler);

        await provider.CompleteAsync(SampleConversation(), null, CancellationToken.None);

        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("local-key", handler.Request.Headers.Authorization.Parameter);
        using var doc = JsonDocument.Parse(handler.RequestBody);
        var r = doc.RootElement;
        Assert.Equal("qwen3:14b", r.GetProperty("model").GetString());
        Assert.Equal("auto", r.GetProperty("tool_choice").GetString());
        Assert.False(r.GetProperty("stream").GetBoolean());
        var messages = r.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user", "assistant", "tool"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(AssistantPrompt.System, messages[0].GetProperty("content").GetString());
        Assert.Equal("=== THE NODE NOW ===\n\nstart the show at 8pm", messages[1].GetProperty("content").GetString());
        Assert.Equal("Checking the playlists.", messages[2].GetProperty("content").GetString());
        var call = messages[2].GetProperty("tool_calls")[0];
        Assert.Equal("toolu_1", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal(AssistantTools.GetState, call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{}", call.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("toolu_1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("{\"seq\":412}", messages[3].GetProperty("content").GetString());
        Assert.DoesNotContain("sig-1", handler.RequestBody, StringComparison.Ordinal);
        var tools = r.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(AssistantTools.Specs.Count, tools.Count);
        Assert.All(tools, t =>
        {
            Assert.Equal("function", t.GetProperty("type").GetString());
            Assert.Equal("object", t.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        });
        Assert.Equal(AssistantTools.ProposeCommands, tools[^1].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task TheLocalClientReadsACannedToolCallAnswer()
    {
        using var handler = new CannedHandler(ToolCallAnswer);
        using var provider = new OpenAiCompatibleProvider(AssistantSettings.Local("qwen3:14b"), null, handler);
        var text = new List<string>();

        var reply = await provider.CompleteAsync(SampleConversation(), text.Add, CancellationToken.None);

        Assert.Null(handler.Request!.Headers.Authorization);
        Assert.Equal(StopKind.ToolUse, reply.Stop);
        var call = Assert.IsType<ToolCallPart>(Assert.Single(reply.Parts));
        Assert.Equal("call_abc", call.Id);
        Assert.Equal(AssistantTools.ProposeCommands, call.Name);
        using var args = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("PLAYLIST NEW Walk-in", args.RootElement.GetProperty("lines")[0].GetString());
        Assert.Equal(new UsageCounts(1200, 40, 1000, 0), reply.Usage);
        Assert.Equal("qwen3:14b", reply.ServedBy);
        Assert.Empty(text);
    }

    [Fact]
    public void ALocalModelsToolCallWrittenAsTextIsReadAsACallAndItsThinkingIsLeftOut()
    {
        const string answer =
            """{"model":"llama3","choices":[{"message":{"role":"assistant","content":"```json\n{\"name\":\"get_state\",\"parameters\":{}}\n```"},"finish_reason":"stop"}]}""";

        var reply = OpenAiCompatibleProvider.ParseResponse(answer, SampleConversation(), "llama3");

        Assert.Equal(StopKind.ToolUse, reply.Stop);
        var call = Assert.IsType<ToolCallPart>(Assert.Single(reply.Parts));
        Assert.Equal(AssistantTools.GetState, call.Name);
        Assert.Equal("call_3_0", call.Id);
        Assert.Equal("All good.", OpenAiCompatibleProvider.StripThinking("<think>hmm</think>All good."));
        var words = OpenAiCompatibleProvider.ParseResponse(
            """{"choices":[{"message":{"role":"assistant","content":"{\"name\":\"not_a_tool\"}"},"finish_reason":"length"}]}""", SampleConversation(), "m");
        Assert.Equal(StopKind.MaxTokens, words.Stop);
        Assert.IsType<TextPart>(Assert.Single(words.Parts));
    }

    [Fact]
    public async Task LocalServerFailuresBecomeWordsTheOperatorCanActOn()
    {
        using var missing = new CannedHandler("""{"error":{"message":"model 'qwen9' not found"}}""", HttpStatusCode.NotFound);
        using var p1 = new OpenAiCompatibleProvider(AssistantSettings.Local("qwen9"), null, missing);
        var notFound = await Assert.ThrowsAsync<AssistantProviderException>(() => p1.CompleteAsync(SampleConversation(), null, CancellationToken.None));
        Assert.Equal(ProviderFailure.NotFound, notFound.Failure);
        Assert.Contains("model 'qwen9' not found", notFound.Message, StringComparison.Ordinal);
        Assert.False(notFound.Retryable);

        using var down = new CannedHandler("") { Throw = new HttpRequestException("Connection refused") };
        using var p2 = new OpenAiCompatibleProvider(AssistantSettings.Local("qwen3"), null, down);
        var network = await Assert.ThrowsAsync<AssistantProviderException>(() => p2.CompleteAsync(SampleConversation(), null, CancellationToken.None));
        Assert.Equal(ProviderFailure.Network, network.Failure);
        Assert.Contains("is its server running", network.Message, StringComparison.Ordinal);
        Assert.True(network.Retryable);

        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleProvider(AssistantSettings.Local("m", "ftp://nowhere"), null, down));
    }

    // ---- Claude ----

    [Fact]
    public void TheClaudeRequestCarriesTheModelTheCachedSystemBlockStrictToolsAndEffort()
    {
        var p = ClaudeProvider.BuildParams(SampleConversation(), AssistantSettings.Default with { Enabled = true });

        Assert.Equal("claude-opus-5-5", p.Model.Raw());
        Assert.Equal(16_000, p.MaxTokens);
        Assert.True(p.System!.TryPickBetaTextBlockParams(out var system));
        var block = Assert.Single(system!);
        Assert.Equal(AssistantPrompt.System, block.Text);
        Assert.NotNull(block.CacheControl);
        Assert.NotNull(p.CacheControl);
        Assert.Equal(AssistantTools.Specs.Count, p.Tools!.Count);
        Assert.All(p.Tools, t =>
        {
            Assert.True(t.TryPickBetaTool(out var tool));
            Assert.True(tool!.Strict);
        });
        Assert.Equal(Effort.Medium, p.OutputConfig!.Effort!.Value());
        Assert.Contains(p.Betas!, b => b.Raw() == "server-side-fallback-2026-07-01");
        Assert.True(p.Fallbacks!.TryPickDefault(out _));
        Assert.Null(p.Thinking);
        Assert.Null(p.ToolChoice);

        using var doc = JsonDocument.Parse(p.ToString());
        var body = doc.RootElement.GetProperty("BodyData");
        Assert.Equal("default", body.GetProperty("fallbacks").GetString());
        Assert.Equal("medium", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("ephemeral", body.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        var tools = body.GetProperty("tools");
        var schema = tools[tools.GetArrayLength() - 1].GetProperty("input_schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("array", schema.GetProperty("properties").GetProperty("lines").GetProperty("type").GetString());
        var messages = body.GetProperty("messages");
        var assistant = messages[1].GetProperty("content");
        Assert.Equal("thinking", assistant[0].GetProperty("type").GetString());
        Assert.Equal("sig-1", assistant[0].GetProperty("signature").GetString());
        Assert.Equal("tool_use", assistant[2].GetProperty("type").GetString());
        Assert.Equal("toolu_1", assistant[2].GetProperty("id").GetString());
        Assert.False(assistant[2].TryGetProperty("caller", out _));
        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("toolu_1", result.GetProperty("tool_use_id").GetString());
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.False(body.TryGetProperty("top_p", out _));
        Assert.False(body.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public void WithTheFallbackTurnedOffTheRequestCarriesNoBeta()
    {
        var p = ClaudeProvider.BuildParams(SampleConversation(), AssistantSettings.Default with { Enabled = true, RefusalFallback = false, Effort = AssistantEffort.High, Model = "claude-sonnet-5-5" });

        Assert.Equal("claude-sonnet-5-5", p.Model.Raw());
        Assert.Equal(Effort.High, p.OutputConfig!.Effort!.Value());
        Assert.Null(p.Betas);
        Assert.Null(p.Fallbacks);
    }

    [Fact]
    public void AClaudeAnswerAfterAFallbackKeepsTheTextAndDropsTheDeclinedModelsCalls()
    {
        var message = Message(
            """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-4-8",
             "content":[{"type":"thinking","thinking":"","signature":"sig-declined"},
                        {"type":"tool_use","id":"toolu_declined","name":"propose_commands","input":{"lines":["BLACKOUT ON"],"reason":"x"}},
                        {"type":"text","text":"Let me check."},
                        {"type":"fallback","from":{"model":"claude-opus-5-5"},"to":{"model":"claude-opus-4-8"}},
                        {"type":"thinking","thinking":"","signature":"sig-2"},
                        {"type":"tool_use","id":"toolu_2","name":"get_state","input":{}}],
             "stop_reason":"tool_use","stop_sequence":null,
             "usage":{"input_tokens":120,"output_tokens":40,"cache_read_input_tokens":9000,"cache_creation_input_tokens":0}}
            """);

        var reply = ClaudeProvider.ReadMessage(message, "claude-opus-5-5");

        Assert.Equal(StopKind.ToolUse, reply.Stop);
        Assert.True(reply.FellBack);
        Assert.Equal("claude-opus-4-8", reply.ServedBy);
        Assert.Equal(new UsageCounts(120, 40, 9000, 0), reply.Usage);
        Assert.Equal(3, reply.Parts.Count);
        Assert.Equal("Let me check.", Assert.IsType<TextPart>(reply.Parts[0]).Text);
        Assert.Equal("sig-2", Assert.IsType<ThinkingPart>(reply.Parts[1]).Signature);
        Assert.Equal("toolu_2", Assert.IsType<ToolCallPart>(reply.Parts[2]).Id);
    }

    [Fact]
    public void AClaudeRefusalCarriesItsCategoryAndExplanation()
    {
        var message = Message(
            """
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],
             "stop_reason":"refusal","stop_sequence":null,
             "stop_details":{"type":"refusal","category":"cyber","explanation":"This request looks like an intrusion."},
             "usage":{"input_tokens":10,"output_tokens":0}}
            """);

        var reply = ClaudeProvider.ReadMessage(message, "claude-opus-5-5");

        Assert.Equal(StopKind.Refusal, reply.Stop);
        Assert.Equal("cyber", reply.RefusalCategory);
        Assert.Equal("This request looks like an intrusion.", reply.RefusalExplanation);
        Assert.False(reply.FellBack);
        Assert.Empty(reply.Parts);
    }

    [Fact]
    public async Task AStreamedClaudeAnswerIsAssembledAndItsTextReachesTheDeskAsItArrives()
    {
        using var transport = new StreamTransport(
            """{"type":"message_start","message":{"id":"msg_3","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1,"cache_read_input_tokens":5000,"cache_creation_input_tokens":0}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"All "}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"green."}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_9","name":"get_state","input":{}}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{}"}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":30}}""",
            """{"type":"message_stop"}""");
        using var provider = new ClaudeProvider(AssistantSettings.Default with { Enabled = true }, transport);
        var text = new List<string>();

        var reply = await provider.CompleteAsync(SampleConversation(), text.Add, CancellationToken.None);

        Assert.Equal(["All ", "green."], text);
        Assert.Equal(StopKind.ToolUse, reply.Stop);
        Assert.Equal("All green.", Assert.IsType<TextPart>(reply.Parts[0]).Text);
        Assert.Equal("toolu_9", Assert.IsType<ToolCallPart>(reply.Parts[1]).Id);
        Assert.Equal(5000, reply.Usage.CacheRead);
        Assert.Equal("claude-opus-5-5", transport.Sent!.Model.Raw());
    }

    [Fact]
    public async Task ClaudeFailuresAreTypedAndSayWhetherAskingAgainCanWork()
    {
        using var limited = new StreamTransport { Throw = new AnthropicRateLimitException(new HttpRequestException("429")) { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "" } };
        using var p1 = new ClaudeProvider(AssistantSettings.Default with { Enabled = true }, limited);
        var rate = await Assert.ThrowsAsync<AssistantProviderException>(() => p1.CompleteAsync(SampleConversation(), null, CancellationToken.None));
        Assert.Equal(ProviderFailure.RateLimited, rate.Failure);
        Assert.True(rate.Retryable);

        using var refused = new StreamTransport { Throw = new AnthropicUnauthorizedException(new HttpRequestException("401")) { StatusCode = HttpStatusCode.Unauthorized, ResponseBody = "" } };
        using var p2 = new ClaudeProvider(AssistantSettings.Default with { Enabled = true }, refused);
        var auth = await Assert.ThrowsAsync<AssistantProviderException>(() => p2.CompleteAsync(SampleConversation(), null, CancellationToken.None));
        Assert.Equal(ProviderFailure.Auth, auth.Failure);
        Assert.False(auth.Retryable);
    }

    [Fact]
    public void TheFactoryMakesNothingWhenTheAssistantIsOff()
    {
        Assert.Null(AssistantProviders.Create(AssistantSettings.Default, "k"));
        Assert.Null(AssistantProviders.Create(AssistantSettings.Default with { Enabled = true, Provider = AssistantProviderKind.None }, null));
        using var local = AssistantProviders.Create(AssistantSettings.Local("qwen3"), null);
        Assert.IsType<OpenAiCompatibleProvider>(local);
        Assert.Equal("http://localhost:11434/v1/chat/completions", ((OpenAiCompatibleProvider)local!).Endpoint.ToString());
    }

    private static BetaMessage Message(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return BetaMessage.FromRawUnchecked(doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
    }

    /// <summary>The SDK's stream, replayed from canned events (or a canned failure).</summary>
    private sealed class StreamTransport : IClaudeTransport
    {
        private readonly string[] _events;

        public StreamTransport(params string[] events) => _events = events;

        public Exception? Throw { get; init; }

        public MessageCreateParams? Sent { get; private set; }

        public async IAsyncEnumerable<BetaRawMessageStreamEvent> StreamAsync(MessageCreateParams parameters, [EnumeratorCancellation] CancellationToken ct)
        {
            Sent = parameters;
            if (Throw is not null)
                throw Throw;
            foreach (var e in _events)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                yield return JsonSerializer.Deserialize<BetaRawMessageStreamEvent>(e)!;
            }
        }

        public void Dispose()
        {
            // Nothing held.
        }
    }
}
