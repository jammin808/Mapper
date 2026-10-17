using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

/// <summary>The assistant's verbs in the demo, with the engine's meaning (EngineCore.Assistant): ASSISTANT STATUS answers
/// the settings in force (never a key), ASSISTANT SET changes one setting from the desk and refuses a value that changes
/// nothing, PROPOSALS answers the empty queue, ASK is the real engine's (the demo has no model). STATE.ai.assistant
/// carries the same object the engine writes.</summary>
public sealed partial class FakeEngineLink
{
    private bool _assistantEnabled = true;
    private string _assistantProvider = "openAiCompatible";
    private string _assistantModel = "show-model";
    private string _assistantEffort = "medium";
    private string _assistantBaseUrl = "http://127.0.0.1:11434/v1";
    private bool _assistantRunLow = true;
    private bool _assistantRunReview;

    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];

    private void WriteAi(Utf8JsonWriter w)
    {
        w.WriteStartObject("ai");
        w.WriteBoolean("mcp", true);
        w.WriteNumber("clients", 0);
        w.WriteNumber("proposals", 0);
        w.WritePropertyName("assistant");
        WriteAssistant(w);
        w.WriteEndObject();
    }

    private void WriteAssistant(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteBoolean("enabled", _assistantEnabled);
        w.WriteString("provider", _assistantProvider);
        w.WriteString("model", _assistantModel);
        w.WriteString("effort", _assistantEffort);
        w.WriteString("baseUrl", _assistantBaseUrl);
        w.WriteBoolean("runLowWithoutAsking", _assistantRunLow);
        w.WriteBoolean("runReviewWithoutAsking", _assistantRunReview);
        w.WriteBoolean("keySet", false);
        w.WriteBoolean("kept", false);
        w.WriteEndObject();
    }

    private string AssistantJson()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
            WriteAssistant(w);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    partial void TryAssistantVerbs(WireLine line, ref WireReply? reply)
    {
        switch (line.Verb)
        {
            case "ASSISTANT" when line.WordUpper(1) == "STATUS":
                reply = Ok(AssistantJson());
                return;
            case "ASSISTANT" when line.WordUpper(1) == "SET":
                reply = AssistantSet(line);
                return;
            case "PROPOSALS":
                reply = Ok("[]");
                return;
            case "APPROVE" or "REJECT":
                reply = Err($"no proposal '{line.Word(1)}' — PROPOSALS lists the ones waiting");
                return;
            case "ASK":
                reply = Err("the demo node has no model to ask — a real engine with an assistant answers ASK");
                return;
        }
    }

    private WireReply AssistantSet(WireLine line)
    {
        const string Syntax = "ASSISTANT SET enabled|provider|model|effort|baseUrl|runLowWithoutAsking|runReviewWithoutAsking <value>";
        if (line.Count != 4)
            return Err(Syntax);
        var value = line.Word(3);
        static bool? Switch(string v) => v.ToUpperInvariant() switch
        {
            "ON" or "TRUE" or "1" => true,
            "OFF" or "FALSE" or "0" => false,
            _ => null,
        };
        switch (line.WordUpper(2))
        {
            case "ENABLED":
                if (Switch(value) is not { } on)
                    return Err("enabled takes on or off");
                if (on == _assistantEnabled)
                    return Err("already so — the assistant is " + (on ? "on" : "off"));
                _assistantEnabled = on;
                return Ok(AssistantJson());
            case "PROVIDER":
                var kind = value.ToUpperInvariant() switch
                {
                    "CLAUDE" => "claude",
                    "OPENAICOMPATIBLE" or "LOCAL" => "openAiCompatible",
                    "NONE" => "none",
                    _ => null,
                };
                if (kind is null)
                    return Err("provider is claude, openAiCompatible (a local model) or none");
                if (kind == _assistantProvider)
                    return Err("already so — the assistant's provider is " + kind);
                _assistantProvider = kind;
                return Ok(AssistantJson());
            case "MODEL":
                if (value.Length is 0 or > 200)
                    return Err("model is the model's name as its provider knows it (1 to 200 characters)");
                if (value == _assistantModel)
                    return Err("already so — the assistant's model is " + value);
                _assistantModel = value;
                return Ok(AssistantJson());
            case "EFFORT":
                var effort = value.ToLowerInvariant();
                if (Array.IndexOf(Efforts, effort) < 0)
                    return Err("effort is low, medium, high, xhigh or max");
                if (effort == _assistantEffort)
                    return Err("already so — the assistant's effort is " + effort);
                _assistantEffort = effort;
                return Ok(AssistantJson());
            case "BASEURL":
                var url = value is "" or "-" ? "" : value;
                if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                    return Err("baseUrl is an http(s) address, or \"\" for the provider's own");
                if (url == _assistantBaseUrl)
                    return Err("already so — that is the endpoint");
                _assistantBaseUrl = url;
                return Ok(AssistantJson());
            case "RUNLOWWITHOUTASKING":
                if (Switch(value) is not { } low)
                    return Err("runLowWithoutAsking takes on or off");
                if (low == _assistantRunLow)
                    return Err("already so");
                _assistantRunLow = low;
                return Ok(AssistantJson());
            case "RUNREVIEWWITHOUTASKING":
                if (Switch(value) is not { } review)
                    return Err("runReviewWithoutAsking takes on or off");
                if (review == _assistantRunReview)
                    return Err("already so");
                _assistantRunReview = review;
                return Ok(AssistantJson());
            default:
                return Err(Syntax);
        }
    }
}
