#pragma warning disable SCME0001 // JsonPatch (experimental): reasoning_content and other fields the SDK does not model
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace MySSH.Ai.Providers.OpenAI;

/// <summary>
/// OpenAI Chat Completions (official OpenAI SDK, NativeAOT-clean) against OpenAI, DeepSeek or any compatible endpoint
/// (Ollama, vLLM, LM Studio, gateways). The SDK sends through the caller's <see cref="HttpClient"/>; the endpoint, key and
/// headers come only from the config (the SDK reads no OPENAI_* variables for these).
/// </summary>
public sealed class OpenAICompatibleProvider : IChatProvider
{
    /// <summary>ApiKeyCredential rejects empty keys; the resulting bearer header is removed again for keyless servers.</summary>
    const string KeylessPlaceholder = "unused";

    static readonly AiToolSpec ProbeTool = new("probe_noop", "Connection test tool. Do not call it.",
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone());

    readonly AiProviderConfig _config;
    readonly OpenAIClient? _client;
    readonly string? _configError;

    public OpenAICompatibleProvider(AiProviderConfig config, string? apiKey, HttpClient http)
        : this(config, apiKey, http, OpenAIProviderOptions.Default) { }

    internal OpenAICompatibleProvider(AiProviderConfig config, string? apiKey, HttpClient http, OpenAIProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _config = config;

        var baseUrl = (config.BaseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            _configError = $"Invalid base URL '{config.BaseUrl}': expected an absolute http(s) URL.";
            return;
        }

        var key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new StatusOnlyRetryPolicy(options),
            NetworkTimeout = options.NetworkTimeout,
            UserAgentApplicationId = "MySSH",
        };
        clientOptions.AddPolicy(new RequestHeadersPolicy(config.ExtraHeaders, keyless: key is null), PipelinePosition.BeforeTransport);
        _client = new OpenAIClient(new ApiKeyCredential(key ?? KeylessPlaceholder), clientOptions);
    }

    public string ProviderId => _config.RawProviderId;

    public ProviderCapabilities Capabilities => _config.Capabilities;

    /// <summary>OpenAI reasoning models only accept max_completion_tokens; elsewhere max_tokens is the common denominator.</summary>
    bool UseMaxCompletionTokens => ProviderId == "openai";

    /// <summary>Official OpenAI never returns reasoning_content; echoing an unknown field there could be rejected.</summary>
    bool EchoReasoning => ProviderId != "openai";

    // ------------------------------------------------------------------ models

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        if (_client is null) throw new AiProviderException(AiErrorKind.InvalidRequest, _configError!);
        try
        {
            var result = await _client.GetOpenAIModelClient().GetModelsAsync(ct).ConfigureAwait(false);
            return [.. result.Value.Where(m => !string.IsNullOrEmpty(m.Id)).Select(m => new ModelInfo(m.Id))];
        }
        catch (Exception ex) when (OpenAIErrors.IsProviderError(ex, ct))
        {
            var c = OpenAIErrors.Classify(ex);
            throw new AiProviderException(c.Kind, "Listing models failed: " + c.Message, c.Status, ex);
        }
    }

    // ------------------------------------------------------------------ chat

    public IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunTurnAsync(request, _config.Capabilities.Tools, _config.Capabilities.Streaming, ct);
    }

    /// <param name="tools">false: plain Q&amp;A, no tools sent (servers / models without function calling).</param>
    /// <param name="streaming">false: one non-streaming completion, replayed as events.</param>
    async IAsyncEnumerable<ChatEvent> RunTurnAsync(ChatRequest request, bool tools, bool streaming, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_client is null)
        {
            yield return new Stopped(StopReason.Error, _configError, null, AiErrorKind.InvalidRequest);
            yield break;
        }

        var messages = OpenAIRequestBuilder.BuildMessages(request, ProviderId, EchoReasoning);
        var options = OpenAIRequestBuilder.BuildOptions(request, tools, UseMaxCompletionTokens);
        var chat = _client.GetChatClient(request.Model);
        var events = streaming ? StreamCoreAsync(chat, messages, options, ct) : CompleteCoreAsync(chat, messages, options, ct);
        await foreach (var ev in events.ConfigureAwait(false)) yield return ev;
    }

    async IAsyncEnumerable<ChatEvent> StreamCoreAsync(ChatClient chat, List<ChatMessage> messages, ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var turn = new TurnState(ProviderId);
        var pending = new List<ChatEvent>();
        Exception? error = null;

        var stream = chat.CompleteChatStreamingAsync(messages, options, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                try
                {
                    if (!await stream.MoveNextAsync().ConfigureAwait(false)) break;
                    turn.Apply(stream.Current, pending);
                }
                catch (ArgumentOutOfRangeException ex) when (turn.AcceptUnknownFinishReason(ex))
                {
                    break; // the finish chunk itself is lost, the turn so far is complete
                }
                catch (Exception ex) when (OpenAIErrors.IsProviderError(ex, ct))
                {
                    error = ex;
                    break;
                }
                foreach (var ev in pending) yield return ev;
                pending.Clear();
                if (turn.StreamError is not null) break;
            }
        }
        finally
        {
            await DisposeQuietlyAsync(stream).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested(); // a cancelled read can surface as an IOException
        if (error is not null)
        {
            var c = OpenAIErrors.Classify(error);
            yield return new Stopped(StopReason.Error, c.Message, null, c.Kind);
            yield break;
        }
        foreach (var ev in turn.Finish()) yield return ev;
    }

    async IAsyncEnumerable<ChatEvent> CompleteCoreAsync(ChatClient chat, List<ChatMessage> messages, ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var turn = new TurnState(ProviderId);
        var pending = new List<ChatEvent>();
        Exception? error = null;
        try
        {
            ChatCompletion completion = await chat.CompleteChatAsync(messages, options, ct).ConfigureAwait(false);
            turn.Apply(completion, pending);
        }
        catch (Exception ex) when (OpenAIErrors.IsProviderError(ex, ct))
        {
            error = ex;
        }

        ct.ThrowIfCancellationRequested();
        if (error is not null)
        {
            var c = OpenAIErrors.Classify(error);
            yield return new Stopped(StopReason.Error, c.Message, null, c.Kind);
            yield break;
        }
        foreach (var ev in pending) yield return ev;
        foreach (var ev in turn.Finish()) yield return ev;
    }

    static async ValueTask DisposeQuietlyAsync(IAsyncDisposable d)
    {
        try
        {
            await d.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException or OperationCanceledException)
        {
            // the response is already broken; nothing to release
        }
    }

    /// <summary>One assistant turn being assembled (streamed updates or a single completion).</summary>
    sealed class TurnState(string providerId)
    {
        readonly string _providerId = providerId;
        readonly StringBuilder _text = new();
        readonly StringBuilder _reasoning = new();
        readonly StringBuilder _refusal = new();
        readonly ToolCallAssembler _tools = new();
        ChatFinishReason? _finish;
        /// <summary>A finish_reason the SDK enum does not know ("eos", "model_length", ...).</summary>
        string? _unknownFinish;
        string? _model;
        Usage? _usage;

        /// <summary>An <c>{"error":...}</c> payload inside the SSE stream (gateways, Ollama, vLLM).</summary>
        public string? StreamError { get; private set; }

        public void Apply(StreamingChatCompletionUpdate u, List<ChatEvent> events)
        {
            if (string.IsNullOrEmpty(_model) && !string.IsNullOrEmpty(u.Model)) _model = u.Model;

            if (u.Patch.TryGetValue("$.error.message"u8, out string? em) && !string.IsNullOrEmpty(em)) { StreamError = em; return; }
            if (u.Patch.TryGetValue("$.error"u8, out string? es) && !string.IsNullOrEmpty(es)) { StreamError = es; return; }

            // DeepSeek / vLLM: reasoning_content; OpenRouter / newer Ollama: reasoning. Not modelled by the SDK.
            if ((u.Patch.TryGetValue("$.choices[0].delta.reasoning_content"u8, out string? rc) && !string.IsNullOrEmpty(rc))
                || (u.Patch.TryGetValue("$.choices[0].delta.reasoning"u8, out rc) && !string.IsNullOrEmpty(rc)))
            {
                _reasoning.Append(rc);
                events.Add(new ThinkingDelta(rc));
            }

            foreach (var part in u.ContentUpdate)
            {
                if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
                {
                    _text.Append(part.Text);
                    events.Add(new TextDelta(part.Text));
                }
            }
            if (!string.IsNullOrEmpty(u.RefusalUpdate)) _refusal.Append(u.RefusalUpdate);

            foreach (var tc in u.ToolCallUpdates)
            {
                var args = tc.FunctionArgumentsUpdate is { IsEmpty: false } bytes ? bytes.ToString() : null;
                _tools.Add(tc.Index, tc.ToolCallId, tc.FunctionName, args, events);
            }

            if (u.FinishReason is { } fr) _finish = fr;
            if (u.Usage is { } usage) _usage = ToUsage(usage, ref u.Patch);
        }

        public void Apply(ChatCompletion c, List<ChatEvent> events)
        {
            _model = c.Model;
            if ((c.Patch.TryGetValue("$.choices[0].message.reasoning_content"u8, out string? rc) && !string.IsNullOrEmpty(rc))
                || (c.Patch.TryGetValue("$.choices[0].message.reasoning"u8, out rc) && !string.IsNullOrEmpty(rc)))
            {
                _reasoning.Append(rc);
                events.Add(new ThinkingDelta(rc));
            }
            foreach (var part in c.Content)
                if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(part.Text)) _text.Append(part.Text);
            if (_text.Length > 0) events.Add(new TextDelta(_text.ToString()));
            if (!string.IsNullOrEmpty(c.Refusal)) _refusal.Append(c.Refusal);
            for (int i = 0; i < c.ToolCalls.Count; i++)
            {
                var tc = c.ToolCalls[i];
                _tools.Add(i, tc.Id, tc.FunctionName, tc.FunctionArguments?.ToString(), events);
            }
            _finish = c.FinishReason;
            if (c.Usage is { } usage) _usage = ToUsage(usage, ref c.Patch);
        }

        /// <summary>
        /// The SDK throws while deserializing a chunk whose finish_reason is not one of OpenAI's values, which some compatible
        /// servers send. Only that exception is accepted (anything else stays an error).
        /// </summary>
        public bool AcceptUnknownFinishReason(ArgumentOutOfRangeException ex)
        {
            if (ex.ActualValue is not string value || !ex.Message.Contains(nameof(ChatFinishReason), StringComparison.Ordinal)) return false;
            _unknownFinish = value;
            return true;
        }

        /// <summary>Normalized like Claude: InputTokens excludes cache reads (OpenAI's prompt_tokens includes them).</summary>
        static Usage ToUsage(ChatTokenUsage u, ref JsonPatch root)
        {
            long cached = u.InputTokenDetails?.CachedTokenCount ?? 0;
            if (cached == 0 && root.TryGetValue("$.usage.prompt_cache_hit_tokens"u8, out long hit)) cached = hit; // DeepSeek
            return new Usage(Math.Max(0, u.InputTokenCount - cached), u.OutputTokenCount, cached, 0);
        }

        public IEnumerable<ChatEvent> Finish()
        {
            if (StreamError is not null)
            {
                yield return new Stopped(StopReason.Error, "The server reported an error: " + StreamError, null, AiErrorKind.Other);
                yield break;
            }
            if (_usage is not null) yield return _usage;
            if (_finish is null && _unknownFinish is null)
            {
                yield return new Stopped(StopReason.Error, "The stream ended without a finish_reason (connection cut or unsupported server).",
                    null, AiErrorKind.MalformedResponse);
                yield break;
            }
            if (_finish == ChatFinishReason.ContentFilter)
            {
                yield return new Stopped(StopReason.Refusal, "content_filter", null);
                yield break;
            }
            if (_refusal.Length > 0 && _text.Length == 0 && _tools.Count == 0)
            {
                yield return new Stopped(StopReason.Refusal, _refusal.ToString(), null);
                yield break;
            }

            var calls = _tools.Build();
            foreach (var call in calls) yield return new ToolCallCompleted(call);

            var text = _text.Length > 0 ? _text.ToString() : null;
            var reasoning = _reasoning.Length > 0 ? _reasoning.ToString() : null;
            var raw = new RawAssistant(text, reasoning, [.. calls.Select(c => new RawToolCall(c.Id, c.Name, c.RawInput))]);
            var reason = _finish switch
            {
                ChatFinishReason.Length => StopReason.MaxTokens,
                null when _unknownFinish!.Contains("length", StringComparison.OrdinalIgnoreCase) => StopReason.MaxTokens,
                // tool_calls, and "stop" / legacy function_call from servers that finish a tool turn that way
                _ when calls.Count > 0 => StopReason.ToolUse,
                _ => StopReason.EndTurn,
            };
            yield return new Stopped(reason, _unknownFinish is null ? null : $"finish_reason '{_unknownFinish}'", new AiMessage
            {
                Role = AiRole.Assistant,
                Text = text,
                ToolCalls = calls,
                Thinking = reasoning,
                RawProvider = _providerId,
                RawContent = raw.ToJson(),
                ServedByModel = string.IsNullOrEmpty(_model) ? null : _model,
            });
        }
    }

    // ------------------------------------------------------------------ probe

    public async Task<ProviderProbeResult> ProbeAsync(string model, CancellationToken ct)
    {
        if (_client is null) return new ProviderProbeResult(false, [], _config.Capabilities, _configError);

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
            // Many compatible servers have no (or a broken) /models: the chat request below decides.
        }

        bool tools = true, streaming = true;
        var first = await AttemptAsync(model, tools, streaming, ct).ConfigureAwait(false);
        var result = first;
        if (!result.Ok && IsToolRejection(result))
        {
            tools = false;
            result = await AttemptAsync(model, tools, streaming, ct).ConfigureAwait(false);
        }
        if (!result.Ok && result.Kind is not (AiErrorKind.Authentication or AiErrorKind.RateLimited or AiErrorKind.Overloaded or AiErrorKind.Network))
        {
            var plain = await AttemptAsync(model, tools, streaming: false, ct).ConfigureAwait(false);
            if (!plain.Ok && tools && IsToolRejection(plain))
            {
                plain = await AttemptAsync(model, tools: false, streaming: false, ct).ConfigureAwait(false);
                if (plain.Ok) tools = false;
            }
            if (plain.Ok)
            {
                streaming = false;
                result = plain;
            }
        }

        return result.Ok
            ? new ProviderProbeResult(true, models, new ProviderCapabilities(tools, streaming, _config.Capabilities.Images))
            : new ProviderProbeResult(false, models, _config.Capabilities, result.Error);
    }

    static bool IsToolRejection(Attempt a) => a.Kind == AiErrorKind.InvalidRequest && OpenAIErrors.MentionsTools(a.Error);

    readonly record struct Attempt(bool Ok, AiErrorKind Kind, string? Error);

    async Task<Attempt> AttemptAsync(string model, bool tools, bool streaming, CancellationToken ct)
    {
        var request = new ChatRequest
        {
            Model = model,
            SystemPrompt = "This is a connection test. Reply with the single word OK.",
            History = [AiMessage.User("ping")],
            Tools = [ProbeTool],
            MaxTokens = 32,
        };
        Stopped? stop = null;
        await foreach (var ev in RunTurnAsync(request, tools, streaming, ct).ConfigureAwait(false))
            if (ev is Stopped s) stop = s;
        return stop is null || stop.Reason == StopReason.Error
            ? new Attempt(false, stop?.ErrorKind ?? AiErrorKind.Other, stop?.Detail ?? "No response.")
            : new Attempt(true, AiErrorKind.None, null);
    }
}
