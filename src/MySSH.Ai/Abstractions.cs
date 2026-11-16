using System.Text.Json;

namespace MySSH.Ai;

// Thin provider abstraction (design §7.2), not Microsoft.Extensions.AI: approvals sit in front of every tool call and
// Claude's native features (adaptive thinking, fallbacks, refusals, thinking blocks replayed verbatim) need the raw shape.

/// <summary>One configured AI service (Claude, OpenAI, DeepSeek, a local OpenAI-compatible server, ...).</summary>
public interface IChatProvider
{
    /// <summary>
    /// Identifies the raw history format this provider produces and accepts (<see cref="AiMessage.RawProvider"/>):
    /// "anthropic", "openai", "deepseek", "openai-compatible". Raw content is replayed only to a provider with the same id.
    /// </summary>
    string ProviderId { get; }

    ProviderCapabilities Capabilities { get; }

    /// <summary>All pages. Throws <see cref="AiProviderException"/> on HTTP / auth / network errors.</summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct);

    /// <summary>
    /// Streams one assistant turn. Never throws for provider errors (HTTP status, network, malformed stream): the stream
    /// then ends with <see cref="Stopped"/> (<see cref="StopReason.Error"/>). Throws <see cref="OperationCanceledException"/>
    /// when <paramref name="ct"/> is cancelled. The last event is always <see cref="Stopped"/>.
    /// </summary>
    IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, CancellationToken ct);

    /// <summary>
    /// "Test connection": lists models, then sends one tiny request with one tool to find out what the endpoint supports.
    /// Never throws for provider errors (reported in the result).
    /// </summary>
    Task<ProviderProbeResult> ProbeAsync(string model, CancellationToken ct);
}

public sealed record ProviderCapabilities(bool Tools = true, bool Streaming = true, bool Images = false);

public sealed record ModelInfo(string Id, string? DisplayName = null);

/// <param name="Ok">The tiny request succeeded.</param>
/// <param name="Error">Why not, ready to show (already includes the HTTP status / provider message).</param>
public sealed record ProviderProbeResult(bool Ok, IReadOnlyList<ModelInfo> Models, ProviderCapabilities Detected, string? Error = null);

/// <summary>A tool offered to the model. <see cref="InputSchema"/> is the JSON Schema from the tool catalog.</summary>
public sealed record AiToolSpec(string Name, string Description, JsonElement InputSchema);

public enum AiEffort { Low, Medium, High }

public sealed record ChatRequest
{
    public required string Model { get; init; }
    /// <summary>Byte-stable for the whole conversation (no timestamps, no per-turn data): prompt caching and Claude's
    /// preserved thinking both depend on an unchanged prefix. Per-turn context goes into the user message.</summary>
    public required string SystemPrompt { get; init; }
    /// <summary>Append-only: earlier entries are never edited or removed.</summary>
    public required IReadOnlyList<AiMessage> History { get; init; }
    /// <summary>The same set, in the same order, for the whole conversation (sorted by name).</summary>
    public IReadOnlyList<AiToolSpec> Tools { get; init; } = [];
    /// <summary>Thinking counts toward it (Claude).</summary>
    public int MaxTokens { get; init; } = 32000;
    /// <summary>Claude only; always sent explicitly.</summary>
    public AiEffort Effort { get; init; } = AiEffort.Medium;
}

// ------------------------------------------------------------------ history (append-only)

public enum AiRole { User, Assistant, ToolResults }

/// <param name="Input">Strictly parsed input; null when <paramref name="InputError"/> is set.</param>
/// <param name="RawInput">The input JSON exactly as streamed.</param>
/// <param name="InputError">Not valid JSON (or cut off): the call is NOT executed; the loop returns
/// <c>{"INVALID_JSON": raw}</c> as an error result so the model can retry.</param>
public sealed record ToolCall(string Id, string Name, JsonElement? Input, string RawInput, string? InputError = null);

public sealed record ToolResult(string ToolCallId, string Content, bool IsError);

/// <summary>
/// One history entry. Assistant turns keep the provider's own content in <see cref="RawContent"/> (Claude: the JSON array
/// of content blocks as received, thinking blocks and signatures included; OpenAI: the assistant message JSON) and are
/// replayed byte-for-byte to the same <see cref="RawProvider"/>. Another provider gets the normalized fields
/// (<see cref="Text"/>, <see cref="ToolCalls"/>) instead.
/// </summary>
public sealed record AiMessage
{
    public required AiRole Role { get; init; }
    /// <summary>User text; for assistant turns the concatenated visible text.</summary>
    public string? Text { get; init; }
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    /// <summary>Same order as the calls of the previous assistant turn.</summary>
    public IReadOnlyList<ToolResult> ToolResults { get; init; } = [];
    public string? RawProvider { get; init; }
    public string? RawContent { get; init; }
    /// <summary>The model that actually produced this turn (differs from the requested one after a server-side fallback).</summary>
    public string? ServedByModel { get; init; }
    /// <summary>Reasoning summary / progress notes shown to the user (not sent back by itself; Claude's blocks travel in RawContent).</summary>
    public string? Thinking { get; init; }

    public static AiMessage User(string text) => new() { Role = AiRole.User, Text = text };
    public static AiMessage Results(IReadOnlyList<ToolResult> results) => new() { Role = AiRole.ToolResults, ToolResults = results };
}

// ------------------------------------------------------------------ normalized stream events

public enum StopReason { EndTurn, ToolUse, MaxTokens, Refusal, Error }

public enum AiErrorKind
{
    None,
    /// <summary>401 / 403: wrong or missing API key.</summary>
    Authentication,
    /// <summary>429.</summary>
    RateLimited,
    /// <summary>529 / 503: provider overloaded.</summary>
    Overloaded,
    /// <summary>400 / 404 / 422: the request was rejected (unknown model, unsupported parameter, ...).</summary>
    InvalidRequest,
    /// <summary>DNS, TLS, connection refused, timeout.</summary>
    Network,
    /// <summary>The response could not be understood.</summary>
    MalformedResponse,
    Other,
}

public abstract record ChatEvent;

public sealed record TextDelta(string Text) : ChatEvent;

/// <summary>Reasoning summary or progress note between tool calls (Claude thinking display, DeepSeek reasoning_content).</summary>
public sealed record ThinkingDelta(string Text) : ChatEvent;

public sealed record ToolCallStarted(string Id, string Name) : ChatEvent;

public sealed record ToolCallInputDelta(string Id, string PartialJson) : ChatEvent;

/// <summary>Input complete and parsed (or <see cref="ToolCall.InputError"/>). Display only: the loop executes the calls
/// of <see cref="Stopped.Message"/>, never these (a refusal or fallback can invalidate calls streamed earlier).</summary>
public sealed record ToolCallCompleted(ToolCall Call) : ChatEvent;

/// <summary>A server-side fallback took over (Claude): <paramref name="FromModel"/> declined, <paramref name="ToModel"/> continues.</summary>
public sealed record FallbackOccurred(string FromModel, string ToModel) : ChatEvent;

public sealed record Usage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens) : ChatEvent;

/// <param name="Detail">Refusal category / explanation, error message, ...</param>
/// <param name="Message">The assistant turn to append to history unchanged (null for errors and for refusals, whose partial
/// output is discarded).</param>
public sealed record Stopped(StopReason Reason, string? Detail, AiMessage? Message, AiErrorKind ErrorKind = AiErrorKind.None) : ChatEvent;

/// <summary>Thrown by <see cref="IChatProvider.ListModelsAsync"/>.</summary>
public sealed class AiProviderException : Exception
{
    public AiProviderException() { }
    public AiProviderException(string message) : base(message) { }
    public AiProviderException(string message, Exception inner) : base(message, inner) { }
    public AiProviderException(AiErrorKind kind, string message, int? status = null, Exception? inner = null) : base(message, inner)
    {
        Kind = kind;
        Status = status;
    }

    public AiErrorKind Kind { get; } = AiErrorKind.Other;
    public int? Status { get; }
}
