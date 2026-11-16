using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;

namespace MySSH.Ai.Providers.OpenAI;

/// <summary>Transport knobs (tests shorten them; production uses <see cref="Default"/>).</summary>
internal sealed record OpenAIProviderOptions
{
    public static OpenAIProviderOptions Default { get; } = new();

    /// <summary>Retries after the first attempt, only for HTTP 429 and 5xx (never for 4xx or network errors).</summary>
    public int MaxRetries { get; init; } = 2;
    /// <summary>Backoff when the server sends no retry-after: base, 2x base, ...</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Upper bound for a server-requested retry-after (a longer wait surfaces as RateLimited instead).</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Per network operation: until the response headers, then per read of a streamed body (idle timeout).
    /// Generous: reasoning models can think for minutes before the first byte.</summary>
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Retries 429 / 5xx only; honours retry-after-ms / retry-after (capped).</summary>
internal sealed class StatusOnlyRetryPolicy(OpenAIProviderOptions options) : ClientRetryPolicy(options.MaxRetries)
{
    readonly OpenAIProviderOptions _options = options;

    protected override bool ShouldRetry(PipelineMessage message, Exception? exception) =>
        IsRetriable(message, exception) && base.ShouldRetry(message, exception);

    protected override async ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception) =>
        IsRetriable(message, exception) && await base.ShouldRetryAsync(message, exception).ConfigureAwait(false);

    static bool IsRetriable(PipelineMessage message, Exception? exception) =>
        exception is null && message.Response is { Status: 429 or (>= 500 and <= 599) };

    protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount)
    {
        if (message.Response is { } response && RetryAfter(response) is { } requested)
            return requested < _options.MaxRetryDelay ? requested : _options.MaxRetryDelay;
        var factor = Math.Pow(2, Math.Max(0, tryCount - 1));
        return TimeSpan.FromTicks((long)(_options.RetryBaseDelay.Ticks * factor));
    }

    static TimeSpan? RetryAfter(PipelineResponse response)
    {
        if (response.Headers.TryGetValue("retry-after-ms", out var ms)
            && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var msValue) && msValue >= 0)
            return TimeSpan.FromMilliseconds(msValue);
        if (response.Headers.TryGetValue("retry-after", out var ra) && !string.IsNullOrWhiteSpace(ra))
        {
            if (double.TryParse(ra, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(seconds);
            if (DateTimeOffset.TryParse(ra, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                var wait = at - DateTimeOffset.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }
        return null;
    }
}

/// <summary>
/// Runs right before the transport (after the SDK's auth policy): adds the configured extra headers (never auth headers:
/// the key has its own field) and removes the placeholder bearer token when no key is configured (keyless servers).
/// </summary>
internal sealed class RequestHeadersPolicy(IReadOnlyDictionary<string, string> extraHeaders, bool keyless) : PipelinePolicy
{
    static readonly string[] Reserved = ["authorization", "x-api-key", "api-key", "host", "content-length"];

    readonly KeyValuePair<string, string>[] _headers = [.. extraHeaders.Where(h => IsAllowed(h.Key))];
    readonly bool _keyless = keyless;

    static bool IsAllowed(string name) =>
        !string.IsNullOrWhiteSpace(name) && !Reserved.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    void Apply(PipelineMessage message)
    {
        if (_keyless) message.Request.Headers.Remove("Authorization");
        foreach (var (name, value) in _headers) message.Request.Headers.Set(name.Trim(), value);
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }
}

/// <summary>Maps SDK / transport exceptions to <see cref="AiErrorKind"/> plus a message ready to show.</summary>
internal static class OpenAIErrors
{
    public readonly record struct Classified(AiErrorKind Kind, string Message, int? Status);

    /// <summary>Everything except caller cancellation counts as a provider error (the stream must not throw).</summary>
    public static bool IsProviderError(Exception ex, CancellationToken ct) =>
        !(ex is OperationCanceledException && ct.IsCancellationRequested);

    public static Classified Classify(Exception ex)
    {
        switch (ex)
        {
            case ClientResultException { Status: 0 } cre:
                return new(AiErrorKind.Network, NetworkMessage(cre.InnerException ?? cre), null);
            case ClientResultException cre:
            {
                var status = cre.Status;
                var server = ServerMessage(cre);
                return new(KindForStatus(status), server is null ? $"HTTP {status}" : $"HTTP {status}: {server}", status);
            }
            case HttpRequestException or IOException or TimeoutException or OperationCanceledException:
                return new(AiErrorKind.Network, NetworkMessage(ex), null);
            case JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException
                or NullReferenceException or InvalidCastException:
                return new(AiErrorKind.MalformedResponse, "Unexpected response from the server: " + ex.Message, null);
            default:
                return new(AiErrorKind.Other, ex.GetType().Name + ": " + ex.Message, null);
        }
    }

    public static AiErrorKind KindForStatus(int status) => status switch
    {
        401 or 403 => AiErrorKind.Authentication,
        429 => AiErrorKind.RateLimited,
        503 or 529 => AiErrorKind.Overloaded,
        400 or 404 or 405 or 413 or 415 or 422 => AiErrorKind.InvalidRequest,
        _ => AiErrorKind.Other,
    };

    static string NetworkMessage(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException => "The request timed out.",
        HttpRequestException { InnerException: { } inner } hre => $"{hre.Message} ({inner.Message})",
        _ => ex.Message,
    };

    /// <summary>OpenAI <c>{"error":{"message"}}</c>, Ollama <c>{"error":"..."}</c>, FastAPI <c>{"detail"}</c>, or the raw
    /// body (shortened).</summary>
    static string? ServerMessage(ClientResultException ex)
    {
        string body;
        try
        {
            body = ex.GetRawResponse()?.Content?.ToString() ?? "";
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        return ExtractMessage(body);
    }

    public static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0]; // some gateways wrap errors
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.String) return err.GetString();
                    if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        var code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()
                            : err.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                        return string.IsNullOrEmpty(code) ? m.GetString() : $"{m.GetString()} ({code})";
                    }
                }
                foreach (var name in (ReadOnlySpan<string>)["message", "detail"])
                    if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
            }
        }
        catch (JsonException)
        {
            // not JSON: fall through to the raw text
        }
        var text = body.Trim();
        return text.Length <= 300 ? text : string.Concat(text.AsSpan(0, 300), "...");
    }

    /// <summary>Used by probe: does a 400 say the endpoint / model cannot do tools?</summary>
    public static bool MentionsTools(string? message) =>
        message is not null && (message.Contains("tool", StringComparison.OrdinalIgnoreCase)
            || message.Contains("function", StringComparison.OrdinalIgnoreCase));
}
