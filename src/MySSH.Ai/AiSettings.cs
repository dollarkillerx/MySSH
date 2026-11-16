using System.Text.Json;
using System.Text.Json.Serialization;

namespace MySSH.Ai;

/// <summary>AI assistant settings (design §7.8), stored as JSON in the settings table under <see cref="Key"/>.
/// API keys are not here: each provider's key is a vault-encrypted secret (<see cref="AiProviderConfig.ApiKeySecretName"/>).</summary>
public sealed record AiSettings
{
    public const string Key = "ai.assistant";

    public IReadOnlyList<AiProviderConfig> Providers { get; init; } = [];
    public Guid? DefaultProviderId { get; init; }

    /// <summary>Mask secrets in everything sent to the provider (on by default).</summary>
    public bool RedactSecrets { get; init; } = true;
    /// <summary>Attach the current host's metadata to new questions (terminal text is only attached when the user asks).</summary>
    public bool AttachHostByDefault { get; init; } = true;
    /// <summary>Lines of terminal text attached by the "terminal" context chip.</summary>
    public int TerminalContextLines { get; init; } = 200;
    public int MaxSteps { get; init; } = 25;
    public int MaxToolOutputChars { get; init; } = 64 * 1024;
    public bool SaveHistory { get; init; } = true;
    /// <summary>Delete conversations not touched for this many days (null = keep).</summary>
    public int? RetentionDays { get; init; }
    /// <summary>Providers the user acknowledged the data notice for ("what you send goes to {provider}").</summary>
    public IReadOnlyList<Guid> DataNoticeAcknowledged { get; init; } = [];

    public AiProviderConfig? DefaultProvider =>
        Providers.FirstOrDefault(p => p.Id == DefaultProviderId) ?? (Providers.Count > 0 ? Providers[0] : null);
}

/// <summary>Source-generated JSON (NativeAOT) for settings and the persisted history.</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AiSettings))]
[JsonSerializable(typeof(AiMessage))]
public sealed partial class AiJsonContext : JsonSerializerContext;

public static class AiHistorySerializer
{
    public static string Serialize(AiMessage message) => JsonSerializer.Serialize(message, AiJsonContext.Default.AiMessage);

    /// <exception cref="JsonException"/>
    public static AiMessage Deserialize(string json) =>
        JsonSerializer.Deserialize(json, AiJsonContext.Default.AiMessage) ?? throw new JsonException("Empty AI history entry.");
}
