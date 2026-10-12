namespace PlayMap.Assistant;

/// <summary>Makes the provider the settings ask for. The key comes from the node's secret store and goes to
/// the provider alone; it is never part of the settings, a log line or a transcript.</summary>
public static class AssistantProviders
{
    /// <summary>The provider, or null when the settings ask for no assistant (off, or no provider) — the app
    /// works fully without one.</summary>
    /// <param name="apiKey">Claude: the API key (null lets the SDK find its own credentials). A local
    /// endpoint: an optional bearer key.</param>
    /// <param name="localHandler">Tests only: the HTTP handler a local provider sends through.</param>
    public static IAssistantProvider? Create(AssistantSettings settings, string? apiKey, HttpMessageHandler? localHandler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings.Normalised();
        if (!s.Enabled)
            return null;
        return s.Provider switch
        {
            AssistantProviderKind.Claude => new ClaudeProvider(s, apiKey),
            AssistantProviderKind.OpenAiCompatible => new OpenAiCompatibleProvider(s, apiKey, localHandler),
            _ => null,
        };
    }
}
