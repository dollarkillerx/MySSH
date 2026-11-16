using System.Net;
using System.Text.Json;

namespace MySSH.Ai.Providers.Anthropic;

/// <summary>HTTP status / API error type → <see cref="AiErrorKind"/>, retry policy and user-facing messages.</summary>
internal static class AnthropicErrors
{
    public static AiErrorKind KindFromStatus(int status) => status switch
    {
        401 or 403 => AiErrorKind.Authentication,
        429 => AiErrorKind.RateLimited,
        529 or 503 => AiErrorKind.Overloaded,
        400 or 404 or 413 or 422 => AiErrorKind.InvalidRequest,
        _ => AiErrorKind.Other,
    };

    /// <summary>The <c>error.type</c> of an error body or SSE <c>error</c> event.</summary>
    public static AiErrorKind KindFromErrorType(string? type) => type switch
    {
        "authentication_error" or "permission_error" => AiErrorKind.Authentication,
        "rate_limit_error" => AiErrorKind.RateLimited,
        "overloaded_error" => AiErrorKind.Overloaded,
        "invalid_request_error" or "not_found_error" or "request_too_large" => AiErrorKind.InvalidRequest,
        _ => AiErrorKind.Other,
    };

    public static bool IsRetryableStatus(int status) => status is 429 or 529 or 503 or 500;

    public static bool IsRetryableKind(AiErrorKind kind) => kind is AiErrorKind.RateLimited or AiErrorKind.Overloaded;

    /// <summary>"HTTP 529 overloaded_error: Overloaded (request-id: req_…)". Falls back to the raw body (truncated).</summary>
    public static string Describe(int status, string? body, string? requestIdHeader)
    {
        string? type = null, message = null, bodyRequestId = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
                    {
                        if (e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) type = t.GetString();
                        if (e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                    }
                    else if (root.TryGetProperty("error", out var es) && es.ValueKind == JsonValueKind.String)
                        message = es.GetString();
                    if (root.TryGetProperty("request_id", out var r) && r.ValueKind == JsonValueKind.String) bodyRequestId = r.GetString();
                }
            }
            catch (JsonException) { }
            if (message is null)
            {
                var trimmed = body.Trim();
                message = trimmed.Length > 300 ? trimmed[..300] + "…" : trimmed;
            }
        }
        var reason = Enum.IsDefined((HttpStatusCode)status) ? ((HttpStatusCode)status).ToString() : null;
        var text = $"HTTP {status}" + (type is not null ? $" {type}" : reason is not null ? $" {reason}" : "");
        if (!string.IsNullOrEmpty(message)) text += ": " + message;
        var requestId = requestIdHeader ?? bodyRequestId;
        if (requestId is not null) text += $" (request-id: {requestId})";
        return text;
    }
}
