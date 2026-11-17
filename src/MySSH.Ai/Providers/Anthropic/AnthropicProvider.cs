using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MySSH.Ai.Providers.Anthropic;

/// <summary>
/// Claude over the Messages API with our own HTTP + SSE client (design §7.2). The official Anthropic C# SDK serializes
/// with reflection and fails under NativeAOT, so requests are written with <see cref="Utf8JsonWriter"/> and responses
/// read with <see cref="JsonDocument"/> / <see cref="System.Text.Json.Nodes.JsonNode"/>.
/// Everything comes from <see cref="AiProviderConfig"/> and the key passed in: no environment variable is ever read
/// (ANTHROPIC_BASE_URL / ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN / ANTHROPIC_CUSTOM_HEADERS have no effect).
/// </summary>
public sealed class AnthropicProvider : IChatProvider
{
    public const string Id = "anthropic";

    /// <summary>Retries of 429 / 529 / 503 / 500 (and retryable stream <c>error</c> events) before anything was yielded.</summary>
    internal const int MaxRetries = 2;

    /// <summary>A longer <c>retry-after</c> is not waited for: the error is reported instead.</summary>
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    readonly AiProviderConfig _config;
    readonly string? _apiKey;
    readonly HttpClient _http;
    readonly string _baseUrl;

    public AnthropicProvider(AiProviderConfig config, string? apiKey, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(http);
        _config = config;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _http = http;
        _baseUrl = (config.BaseUrl ?? "").Trim().TrimEnd('/');
    }

    public string ProviderId => Id;

    public ProviderCapabilities Capabilities => _config.Capabilities;

    /// <summary>Test seam: how retry delays are awaited.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Test seam: first backoff step (doubled per attempt, plus up to 25% jitter) when there is no retry-after.</summary>
    internal TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    // ================================================================== streaming

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (!TryUri("/v1/messages", out var uri, out var uriError))
        {
            yield return new Stopped(StopReason.Error, uriError, null, AiErrorKind.InvalidRequest);
            yield break;
        }
        var body = AnthropicRequestBuilder.BuildMessagesBody(_config, request);
        var betas = AnthropicRequestBuilder.Betas(_config);

        for (var attempt = 0; ; attempt++)
        {
            var sent = await SendAsync(() => CreateRequest(HttpMethod.Post, uri, body, betas), ct).ConfigureAwait(false);
            if (sent.Failure is { } failure)
            {
                if (await WaitForRetryAsync(failure, attempt, ct).ConfigureAwait(false)) continue;
                yield return failure.ToStopped();
                yield break;
            }

            using var response = sent.Response!;
            var assembler = new AnthropicStreamAssembler();
            var buffer = new List<ChatEvent>();

            if (!IsEventStream(response))
            {
                // Some gateways ignore stream:true and answer with the complete message.
                var whole = await ReadWholeMessageAsync(response, assembler, buffer, ct).ConfigureAwait(false);
                foreach (var e in buffer) yield return e;
                yield return whole;
                yield break;
            }

            var opened = await OpenStreamAsync(response, ct).ConfigureAwait(false);
            if (opened.Failure is { } openFailure)
            {
                yield return openFailure;
                yield break;
            }

            var yielded = false;
            var retry = false;
            Stopped? final = null;
            // The HttpClient has no overall timeout (answers can stream for minutes), so a stalled stream needs its own:
            // the API sends ping events while it works, and nothing at all for StreamIdleTimeout means the connection died.
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await using (var events = SseReader.ReadAsync(opened.Stream!, idle.Token).GetAsyncEnumerator(idle.Token))
            {
                while (final is null)
                {
                    idle.CancelAfter(StreamIdleTimeout);
                    var step = await NextAsync(events, assembler, buffer, ct).ConfigureAwait(false);
                    if (step is { Reason: StopReason.Error } && !yielded && buffer.Count == 0 && attempt < MaxRetries
                        && AnthropicErrors.IsRetryableKind(step.ErrorKind))
                    {
                        retry = true; // e.g. an overloaded_error event before any output
                        break;
                    }
                    foreach (var e in buffer)
                    {
                        yielded = true;
                        yield return e;
                    }
                    buffer.Clear();
                    final = step;
                }
            }
            if (retry)
            {
                await Delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }
            yield return final!;
            yield break;
        }
    }

    /// <summary>No byte for this long ends the turn with a network error (the server pings every few seconds).</summary>
    internal static TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Reads one SSE event. Returns null to continue, else the final event (normal end or error).</summary>
    static async Task<Stopped?> NextAsync(IAsyncEnumerator<SseEvent> events, AnthropicStreamAssembler assembler, List<ChatEvent> buffer,
        CancellationToken ct)
    {
        try
        {
            if (!await events.MoveNextAsync().ConfigureAwait(false))
                return assembler.Finish(buffer);
            if (assembler.Feed(events.Current, buffer) is { } error)
                return error;
            return assembler.Completed ? assembler.Finish(buffer) : null;
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not InvalidDataException)
        {
            if (ex is OperationCanceledException) throw;
            throw new OperationCanceledException("Cancelled.", ex, ct);
        }
        catch (InvalidDataException ex)
        {
            return new Stopped(StopReason.Error, ex.Message, null, AiErrorKind.MalformedResponse);
        }
        catch (OperationCanceledException ex)
        {
            return new Stopped(StopReason.Error, "The response stream timed out: " + ex.Message, null, AiErrorKind.Network);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return new Stopped(StopReason.Error, "The connection failed while streaming: " + ex.Message, null, AiErrorKind.Network);
        }
    }

    static async Task<(Stream? Stream, Stopped? Failure)> OpenStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return (await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            return (null, new Stopped(StopReason.Error, "The connection failed: " + ex.Message, null, AiErrorKind.Network));
        }
    }

    static async Task<Stopped> ReadWholeMessageAsync(HttpResponseMessage response, AnthropicStreamAssembler assembler, List<ChatEvent> buffer,
        CancellationToken ct)
    {
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            return new Stopped(StopReason.Error, "The connection failed: " + ex.Message, null, AiErrorKind.Network);
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            assembler.FeedMessage(doc.RootElement, buffer);
            return assembler.Finish(buffer);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            buffer.Clear();
            var ctype = response.Content.Headers.ContentType?.MediaType ?? "no content type";
            return new Stopped(StopReason.Error, $"Unexpected response ({ctype}): {ex.Message}", null, AiErrorKind.MalformedResponse);
        }
    }

    static bool IsEventStream(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType is not { } media
        || media.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase);

    // ================================================================== models

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var models = new List<ModelInfo>();
        string? after = null;
        for (var page = 0; page < 100; page++)
        {
            var path = "/v1/models?limit=100" + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after));
            if (!TryUri(path, out var uri, out var uriError))
                throw new AiProviderException(AiErrorKind.InvalidRequest, uriError);

            HttpResponseMessage response;
            for (var attempt = 0; ; attempt++)
            {
                var sent = await SendAsync(() => CreateRequest(HttpMethod.Get, uri, null, []), ct).ConfigureAwait(false);
                if (sent.Failure is { } failure)
                {
                    if (await WaitForRetryAsync(failure, attempt, ct).ConfigureAwait(false)) continue;
                    throw new AiProviderException(failure.Kind, failure.Message, failure.Status);
                }
                response = sent.Response!;
                break;
            }

            string text;
            using (response)
            {
                try
                {
                    text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
                {
                    throw new AiProviderException(AiErrorKind.Network, "The connection failed: " + ex.Message, null, ex);
                }
            }

            bool hasMore;
            string? lastId;
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new AiProviderException(AiErrorKind.MalformedResponse, "Unexpected model list response: no 'data' array.");
                foreach (var m in data.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                        continue;
                    var display = m.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : null;
                    models.Add(new ModelInfo(id.GetString()!, display));
                }
                hasMore = root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
                lastId = root.TryGetProperty("last_id", out var li) && li.ValueKind == JsonValueKind.String ? li.GetString() : null;
            }
            catch (JsonException ex)
            {
                throw new AiProviderException(AiErrorKind.MalformedResponse, "Unexpected model list response: " + ex.Message, null, ex);
            }

            if (!hasMore || string.IsNullOrEmpty(lastId) || lastId == after)
                break;
            after = lastId;
        }
        return models;
    }

    // ================================================================== probe

    public async Task<ProviderProbeResult> ProbeAsync(string model, CancellationToken ct)
    {
        IReadOnlyList<ModelInfo> models = [];
        try
        {
            models = await ListModelsAsync(ct).ConfigureAwait(false);
        }
        catch (AiProviderException ex) when (ex.Kind is AiErrorKind.Authentication or AiErrorKind.Network)
        {
            return new ProviderProbeResult(false, [], _config.Capabilities, ex.Message);
        }
        catch (AiProviderException)
        {
            // Some gateways have no /v1/models: the request below decides.
        }

        var withTools = await ProbeRequestAsync(model, tools: true, ct).ConfigureAwait(false);
        if (withTools.Reason != StopReason.Error)
            return new ProviderProbeResult(true, models, _config.Capabilities with { Tools = true, Streaming = true });

        if (withTools.ErrorKind == AiErrorKind.InvalidRequest)
        {
            // The endpoint may reject tools (or one of the tool fields): try once without.
            var plain = await ProbeRequestAsync(model, tools: false, ct).ConfigureAwait(false);
            if (plain.Reason != StopReason.Error)
                return new ProviderProbeResult(true, models, _config.Capabilities with { Tools = false, Streaming = true });
        }
        return new ProviderProbeResult(false, models, _config.Capabilities, withTools.Detail ?? "The test request failed.");
    }

    static readonly AiToolSpec ProbeTool = new(
        "connection_test",
        "Connection test tool. Do not call it.",
        JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""").RootElement.Clone());

    async Task<Stopped> ProbeRequestAsync(string model, bool tools, CancellationToken ct)
    {
        var request = new ChatRequest
        {
            Model = model,
            SystemPrompt = "Connection test.",
            History = [AiMessage.User("Reply with the single word OK.")],
            Tools = tools ? [ProbeTool] : [],
            // Thinking is always on and counts toward max_tokens; at low effort this is plenty for "OK".
            MaxTokens = 1024,
            Effort = AiEffort.Low,
        };
        Stopped? last = null;
        await foreach (var e in StreamAsync(request, ct).ConfigureAwait(false))
            if (e is Stopped s) last = s;
        return last ?? new Stopped(StopReason.Error, "No response.", null, AiErrorKind.MalformedResponse);
    }

    // ================================================================== HTTP

    sealed record HttpFailure(AiErrorKind Kind, string Message, int? Status, TimeSpan? RetryAfter = null, bool? ShouldRetry = null)
    {
        public Stopped ToStopped() => new(StopReason.Error, Message, null, Kind);
    }

    readonly record struct SendResult(HttpResponseMessage? Response, HttpFailure? Failure);

    bool TryUri(string pathAndQuery, out Uri uri, out string error)
    {
        if (Uri.TryCreate(_baseUrl + pathAndQuery, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
        {
            uri = u;
            error = "";
            return true;
        }
        uri = null!;
        error = $"Invalid base URL '{_config.BaseUrl}': an absolute http(s) URL is required.";
        return false;
    }

    HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, byte[]? body, IReadOnlyList<string> betas)
    {
        var msg = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            msg.Content = new ByteArrayContent(body);
            msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        AnthropicRequestBuilder.ApplyHeaders(msg, _config, _apiKey, betas);
        return msg;
    }

    /// <summary>One attempt. HTTP error statuses are read and returned as failures; the success response is returned
    /// unread (headers only), to be disposed by the caller.</summary>
    async Task<SendResult> SendAsync(Func<HttpRequestMessage> create, CancellationToken ct)
    {
        using var request = create();
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            return new(null, new HttpFailure(AiErrorKind.Network, "The request timed out: " + ex.Message, null));
        }
        catch (HttpRequestException ex)
        {
            return new(null, new HttpFailure(AiErrorKind.Network, "Could not connect to " + request.RequestUri?.GetLeftPart(UriPartial.Authority) + ": " + ex.Message, null));
        }

        if (response.IsSuccessStatusCode)
            return new(response, null);

        using (response)
        {
            var status = (int)response.StatusCode;
            string? body = null;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) { }

            var requestId = response.Headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() : null;
            bool? shouldRetry = response.Headers.TryGetValues("x-should-retry", out var sr)
                ? sr.FirstOrDefault()?.Trim().ToUpperInvariant() switch { "TRUE" => true, "FALSE" => false, _ => null }
                : null;
            return new(null, new HttpFailure(AnthropicErrors.KindFromStatus(status), AnthropicErrors.Describe(status, body, requestId), status,
                RetryAfter(response), shouldRetry));
        }
    }

    async Task<bool> WaitForRetryAsync(HttpFailure failure, int attempt, CancellationToken ct)
    {
        if (attempt >= MaxRetries || failure.Status is not { } status || !AnthropicErrors.IsRetryableStatus(status) || failure.ShouldRetry == false)
            return false;
        var delay = failure.RetryAfter ?? Backoff(attempt);
        if (delay > MaxRetryAfter)
            return false;
        await Delay(delay, ct).ConfigureAwait(false);
        return true;
    }

    TimeSpan Backoff(int attempt)
    {
        var baseMs = BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt);
#pragma warning disable CA5394 // jitter, not security
        var jitter = baseMs * 0.25 * Random.Shared.NextDouble();
#pragma warning restore CA5394
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }

    static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out var ms)
            && double.TryParse(ms.FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var msValue)
            && msValue >= 0)
            return TimeSpan.FromMilliseconds(msValue);
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (ra?.Date is { } date)
        {
            var d = date - DateTimeOffset.UtcNow;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }
        return null;
    }
}
