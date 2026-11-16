namespace MySSH.Ai;

/// <summary>Which preset a provider was created from (design §7.1).</summary>
public enum AiProviderKind { Claude = 0, OpenAI = 1, DeepSeek = 2, Custom = 3 }

/// <summary>Wire protocol. Custom providers pick one.</summary>
public enum AiProtocol { Anthropic = 0, OpenAICompatible = 1 }

/// <summary>Claude thinking display: reasoning summary, short progress notes only, or nothing.</summary>
public enum AiThinkingDisplay { Summarized = 0, Updates = 1, Omitted = 2 }

/// <summary>
/// A configured provider (stored in settings; the API key lives in the encrypted secret store under
/// <see cref="ApiKeySecretName"/>, never here).
/// </summary>
public sealed record AiProviderConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public AiProviderKind Kind { get; init; }
    public AiProtocol Protocol { get; init; }
    /// <summary>Always explicit; never read from the environment (ANTHROPIC_BASE_URL etc. are ignored).</summary>
    public required string BaseUrl { get; init; }
    public string? Model { get; init; }
    /// <summary>Extra request headers (custom gateways). Never Authorization / x-api-key: the key has its own field.</summary>
    public IReadOnlyDictionary<string, string> ExtraHeaders { get; init; } = new Dictionary<string, string>();
    public ProviderCapabilities Capabilities { get; init; } = new();
    public AiEffort Effort { get; init; } = AiEffort.Medium;
    public AiThinkingDisplay ThinkingDisplay { get; init; } = AiThinkingDisplay.Summarized;
    /// <summary>Claude: <c>fallbacks: "default"</c> (beta server-side-fallback-2026-07-01). On by default.</summary>
    public bool ServerSideFallback { get; init; } = true;
    /// <summary>Claude: <c>eager_input_streaming</c> on tools. Off for custom gateways unless confirmed (they may reject it).</summary>
    public bool EagerInputStreaming { get; init; } = true;
    /// <summary>Claude: send the API key as <c>x-api-key</c> (default) or <c>Authorization: Bearer</c> (some gateways).</summary>
    public bool UseBearerAuth { get; init; }
    /// <summary>Output token cap per response (thinking included for Claude). Null = 32000. Some servers cap lower.</summary>
    public int? MaxOutputTokens { get; init; }
    /// <summary>Set after a successful "Test connection".</summary>
    public bool Verified { get; init; }

    public string ApiKeySecretName => $"ai.provider.{Id:N}.key";

    public static AiProviderConfig Claude() => new()
    {
        Name = "Claude", Kind = AiProviderKind.Claude, Protocol = AiProtocol.Anthropic,
        BaseUrl = "https://api.anthropic.com", Model = "claude-opus-5-5",
    };

    /// <summary>The model is picked from the list after the key is entered (no hard-coded default).</summary>
    public static AiProviderConfig OpenAI() => new()
    {
        Name = "OpenAI", Kind = AiProviderKind.OpenAI, Protocol = AiProtocol.OpenAICompatible,
        BaseUrl = "https://api.openai.com/v1",
    };

    public static AiProviderConfig DeepSeek() => new()
    {
        Name = "DeepSeek", Kind = AiProviderKind.DeepSeek, Protocol = AiProtocol.OpenAICompatible,
        BaseUrl = "https://api.deepseek.com", MaxOutputTokens = 8192,
    };

    /// <summary>E.g. Ollama at http://127.0.0.1:11434/v1 (no key needed).</summary>
    public static AiProviderConfig Custom(AiProtocol protocol, string baseUrl) => new()
    {
        Name = "Custom", Kind = AiProviderKind.Custom, Protocol = protocol, BaseUrl = baseUrl,
        EagerInputStreaming = false,
    };

    /// <summary>The raw-history id this config's provider uses (see <see cref="IChatProvider.ProviderId"/>).</summary>
    public string RawProviderId => (Protocol, Kind) switch
    {
        (AiProtocol.Anthropic, _) => "anthropic",
        (_, AiProviderKind.OpenAI) => "openai",
        (_, AiProviderKind.DeepSeek) => "deepseek",
        _ => "openai-compatible",
    };
}

/// <summary>Creates provider instances. <paramref name="http"/> is shared and owned by the caller (proxy, timeouts).</summary>
public static class AiProviders
{
    public static IChatProvider Create(AiProviderConfig config, string? apiKey, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Protocol switch
        {
            AiProtocol.Anthropic => new Providers.Anthropic.AnthropicProvider(config, apiKey, http),
            _ => new Providers.OpenAI.OpenAICompatibleProvider(config, apiKey, http),
        };
    }
}
