using System.Text.Json;

namespace AiSpike.Core;

/// <summary>Thin provider abstraction (design §7.2). Not Microsoft.Extensions.AI on purpose.</summary>
public interface IChatProvider
{
    /// <summary>Stable id stored with raw content, e.g. "anthropic", "openai", "deepseek".</summary>
    string ProviderId { get; }
    ProviderCapabilities Capabilities { get; }
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct);

    /// <summary>
    /// Streams one assistant turn. The last event is always <see cref="Stopped"/>, which carries the
    /// assistant message (normalized + provider raw content) to append to history unchanged.
    /// </summary>
    IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest req, CancellationToken ct);
}

public sealed record ProviderCapabilities(bool Tools, bool Streaming, bool Images = false);

public sealed record ModelInfo(string Id, string? DisplayName = null);

public sealed record ToolDefinition(string Name, string Description, JsonElement InputSchema);

public sealed record ChatRequest
{
    public required string Model { get; init; }
    public required string SystemPrompt { get; init; }
    public required IReadOnlyList<AiMessage> History { get; init; }
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
    public int MaxTokens { get; init; } = 64000;
    /// <summary>"low" | "medium" | "high" (Claude only). Default is explicit medium.</summary>
    public string Effort { get; init; } = "medium";
}

// ---------------- History (append-only) ----------------

public enum AiRole { User, Assistant, ToolResults }

public sealed record ToolCall(string Id, string Name, JsonElement? Input, string RawInput, string? InputError);

public sealed record ToolResult(string ToolCallId, string Content, bool IsError);

/// <summary>
/// One history entry. For assistant turns, <see cref="RawContent"/> holds the provider-native content
/// exactly as received (Anthropic: JSON array of content blocks incl. thinking+signature; OpenAI: the
/// assistant message JSON). Replay to the same provider sends RawContent back unchanged; replay to a
/// different provider degrades to the normalized fields (Text / ToolCalls).
/// </summary>
public sealed record AiMessage
{
    public required AiRole Role { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    public IReadOnlyList<ToolResult> ToolResults { get; init; } = [];
    public string? RawProvider { get; init; }
    public string? RawContent { get; init; }
    public string? ServedByModel { get; init; }

    public static AiMessage User(string text) => new() { Role = AiRole.User, Text = text };
    public static AiMessage Results(IReadOnlyList<ToolResult> results) => new() { Role = AiRole.ToolResults, ToolResults = results };
}

// ---------------- Normalized events ----------------

public enum StopReason { EndTurn, ToolUse, MaxTokens, Refusal, Error }

public abstract record ChatEvent;
public sealed record TextDelta(string Text) : ChatEvent;
public sealed record ThinkingSummaryDelta(string Text) : ChatEvent;
public sealed record ToolCallStarted(string Id, string Name) : ChatEvent;
public sealed record ToolCallInputDelta(string Id, string PartialJson) : ChatEvent;
/// <summary>Input parsed strictly + schema-validated. <see cref="ToolCall.InputError"/> non-null means "do not execute".</summary>
public sealed record ToolCallCompleted(ToolCall Call) : ChatEvent;
public sealed record Usage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens) : ChatEvent;
public sealed record Stopped(StopReason Reason, string? Detail, AiMessage? Message) : ChatEvent;
