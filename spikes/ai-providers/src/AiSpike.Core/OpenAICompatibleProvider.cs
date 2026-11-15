#pragma warning disable SCME0001 // JsonPatch (experimental) - used only for reasoning_content
using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;

namespace AiSpike.Core;

public sealed record OpenAICompatibleOptions
{
    public required string ProviderId { get; init; }
    public required string BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public ProviderCapabilities Capabilities { get; init; } = new(Tools: true, Streaming: true);
    /// <summary>OpenAI strict function schemas. Off by default: many compatible servers reject/ignore it.</summary>
    public bool StrictTools { get; init; }
    public int? MaxRetries { get; init; }

    public static OpenAICompatibleOptions OpenAI(string? apiKey) => new() { ProviderId = "openai", BaseUrl = "https://api.openai.com/v1", ApiKey = apiKey };
    public static OpenAICompatibleOptions DeepSeek(string? apiKey) => new() { ProviderId = "deepseek", BaseUrl = "https://api.deepseek.com", ApiKey = apiKey };
}

/// <summary>OpenAI Chat Completions (official OpenAI SDK) against any compatible endpoint.</summary>
public sealed class OpenAICompatibleProvider : IChatProvider
{
    private readonly OpenAICompatibleOptions _opts;
    private readonly OpenAIClientOptions _clientOptions;
    private readonly ApiKeyCredential _credential;

    public OpenAICompatibleProvider(OpenAICompatibleOptions opts)
    {
        _opts = opts;
        _clientOptions = new OpenAIClientOptions { Endpoint = new Uri(opts.BaseUrl) };
        if (opts.MaxRetries is { } r)
            _clientOptions.RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(r);
        // ApiKeyCredential rejects empty strings; local models (Ollama etc.) accept any bearer value.
        _credential = new ApiKeyCredential(string.IsNullOrEmpty(opts.ApiKey) ? "unused" : opts.ApiKey);
    }

    public string ProviderId => _opts.ProviderId;
    public ProviderCapabilities Capabilities => _opts.Capabilities;

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var client = new OpenAIModelClient(_credential, _clientOptions);
        OpenAIModelCollection models = await client.GetModelsAsync(ct);
        return models.Select(m => new ModelInfo(m.Id, m.OwnedBy)).ToList();
    }

    public (List<ChatMessage> Messages, ChatCompletionOptions Options) BuildRequest(ChatRequest req)
    {
        var messages = new List<ChatMessage> { new SystemChatMessage(req.SystemPrompt) };
        foreach (var m in req.History)
        {
            switch (m.Role)
            {
                case AiRole.User:
                    messages.Add(new UserChatMessage(m.Text ?? ""));
                    break;
                case AiRole.ToolResults:
                    // Chat Completions has no is_error flag: encode it in the content.
                    foreach (var r in m.ToolResults)
                        messages.Add(new ToolChatMessage(r.ToolCallId, r.IsError ? "ERROR: " + r.Content : r.Content));
                    break;
                case AiRole.Assistant:
                    messages.Add(ToAssistantMessage(m));
                    break;
            }
        }

        var options = new ChatCompletionOptions { MaxOutputTokenCount = null };
        if (_opts.Capabilities.Tools && req.Tools.Count > 0)
        {
            foreach (var t in req.Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
                options.Tools.Add(ChatTool.CreateFunctionTool(t.Name, t.Description,
                    BinaryData.FromString(t.InputSchema.GetRawText()), _opts.StrictTools ? true : null));
            options.ToolChoice = ChatToolChoice.CreateAutoChoice();
            options.AllowParallelToolCalls = true;
        }
        return (messages, options);
    }

    private AssistantChatMessage ToAssistantMessage(AiMessage m)
    {
        string? text = m.Text;
        IReadOnlyList<ToolCall> calls = m.ToolCalls;
        string? reasoning = null;
        if (m.RawProvider == ProviderId && m.RawContent is not null)
        {
            // Raw content is the assistant message JSON we assembled from the stream (wire shape).
            var node = JsonNode.Parse(m.RawContent)!.AsObject();
            text = node["content"]?.GetValue<string>();
            reasoning = node["reasoning_content"]?.GetValue<string>();
            calls = (node["tool_calls"]?.AsArray() ?? [])
                .Select(tc => new ToolCall(tc!["id"]!.GetValue<string>(), tc["function"]!["name"]!.GetValue<string>(), null,
                    tc["function"]!["arguments"]!.GetValue<string>(), null))
                .ToList();
        }

        AssistantChatMessage msg;
        if (calls.Count > 0)
        {
            msg = new AssistantChatMessage(calls.Select(c =>
                ChatToolCall.CreateFunctionToolCall(c.Id, c.Name, BinaryData.FromString(c.RawInput.Length == 0 ? "{}" : c.RawInput))));
            if (!string.IsNullOrEmpty(text)) msg.Content.Add(ChatMessageContentPart.CreateTextPart(text));
        }
        else
        {
            msg = new AssistantChatMessage(text ?? "");
        }
        // DeepSeek thinking-mode tool loops expect reasoning_content echoed back on the assistant
        // message. The SDK has no property for it; JsonPatch (experimental) writes an extra field.
        if (reasoning is not null) msg.Patch.Set("$.reasoning_content"u8, reasoning);
        return msg;
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var (messages, options) = BuildRequest(req);
        var client = new ChatClient(req.Model, _credential, _clientOptions);
        var tools = _opts.Capabilities.Tools ? req.Tools : [];

        if (!_opts.Capabilities.Streaming)
        {
            await foreach (var ev in CompleteOnce(client, messages, options, tools, ct)) yield return ev;
            yield break;
        }

        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var frags = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
        ChatFinishReason? finish = null;
        string? model = null;
        Exception? error = null;

        await using var e = client.CompleteChatStreamingAsync(messages, options, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            StreamingChatCompletionUpdate u;
            try
            {
                if (!await e.MoveNextAsync()) break;
                u = e.Current;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (ClientResultException ex) { error = ex; break; }
            catch (HttpRequestException ex) { error = ex; break; }

            model ??= u.Model;
            foreach (var part in u.ContentUpdate)
                if (part.Kind == ChatMessageContentPartKind.Text && part.Text.Length > 0)
                {
                    text.Append(part.Text);
                    yield return new TextDelta(part.Text);
                }

            // DeepSeek-style reasoning_content: not modeled by the SDK; read via JsonPatch.
            if (u.Patch.TryGetValue("$.choices[0].delta.reasoning_content"u8, out string? rc) && !string.IsNullOrEmpty(rc))
            {
                reasoning.Append(rc);
                yield return new ThinkingSummaryDelta(rc);
            }

            foreach (var tc in u.ToolCallUpdates)
            {
                if (!frags.TryGetValue(tc.Index, out var f))
                {
                    f = (null, null, new StringBuilder());
                }
                bool justNamed = false;
                if (tc.ToolCallId is { Length: > 0 } id && f.Id is null) f.Id = id;
                if (tc.FunctionName is { Length: > 0 } name && f.Name is null) { f.Name = name; justNamed = true; }
                frags[tc.Index] = f;
                if (justNamed) yield return new ToolCallStarted(f.Id ?? $"call_{tc.Index}", f.Name!);
                if (tc.FunctionArgumentsUpdate is { } argBytes && !argBytes.IsEmpty)
                {
                    var s = argBytes.ToString();
                    f.Args.Append(s);
                    yield return new ToolCallInputDelta(f.Id ?? $"call_{tc.Index}", s);
                }
            }

            if (u.FinishReason is { } fr) finish = fr;
            if (u.Usage is { } usage)
                yield return new Usage(usage.InputTokenCount, usage.OutputTokenCount,
                    usage.InputTokenDetails?.CachedTokenCount ?? 0, 0);
        }

        if (error is not null)
        {
            yield return new Stopped(StopReason.Error, error.GetType().Name + ": " + error.Message, null);
            yield break;
        }

        var calls = new List<ToolCall>();
        foreach (var (idx, f) in frags)
        {
            var call = ToolInput.Build(f.Id ?? $"call_{idx}", f.Name ?? "", f.Args.ToString(), tools);
            calls.Add(call);
            yield return new ToolCallCompleted(call);
        }

        yield return Finish(finish, text.ToString(), reasoning.Length > 0 ? reasoning.ToString() : null, calls, model);
    }

    private async IAsyncEnumerable<ChatEvent> CompleteOnce(ChatClient client, List<ChatMessage> messages,
        ChatCompletionOptions options, IReadOnlyList<ToolDefinition> tools, [EnumeratorCancellation] CancellationToken ct)
    {
        ChatCompletion completion;
        string? err = null;
        try
        {
            completion = (await client.CompleteChatAsync(messages, options, ct)).Value;
        }
        catch (ClientResultException ex) { err = ex.Message; completion = null!; }
        if (err is not null) { yield return new Stopped(StopReason.Error, err, null); yield break; }

        string text = string.Concat(completion.Content.Where(p => p.Kind == ChatMessageContentPartKind.Text).Select(p => p.Text));
        string? reasoning = completion.Patch.TryGetValue("$.choices[0].message.reasoning_content"u8, out string? rc) ? rc : null;
        if (!string.IsNullOrEmpty(reasoning)) yield return new ThinkingSummaryDelta(reasoning);
        if (text.Length > 0) yield return new TextDelta(text);
        var calls = new List<ToolCall>();
        foreach (var tc in completion.ToolCalls)
        {
            yield return new ToolCallStarted(tc.Id, tc.FunctionName);
            var call = ToolInput.Build(tc.Id, tc.FunctionName, tc.FunctionArguments.ToString(), tools);
            calls.Add(call);
            yield return new ToolCallCompleted(call);
        }
        if (completion.Usage is { } u) yield return new Usage(u.InputTokenCount, u.OutputTokenCount, u.InputTokenDetails?.CachedTokenCount ?? 0, 0);
        yield return Finish(completion.FinishReason, text, reasoning, calls, completion.Model);
    }

    private Stopped Finish(ChatFinishReason? finish, string text, string? reasoning, List<ToolCall> calls, string? model)
    {
        // Raw content = assistant message in Chat Completions wire shape (what we replay).
        var raw = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text.Length > 0 ? text : null,
        };
        if (reasoning is not null) raw["reasoning_content"] = reasoning;
        if (calls.Count > 0)
            raw["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.RawInput },
            }).ToArray());

        var msg = new AiMessage
        {
            Role = AiRole.Assistant,
            Text = text,
            ToolCalls = calls,
            RawProvider = ProviderId,
            RawContent = raw.ToJsonString(),
            ServedByModel = model,
        };
        var reason = finish switch
        {
            ChatFinishReason.ToolCalls => StopReason.ToolUse,
            ChatFinishReason.Length => StopReason.MaxTokens,
            ChatFinishReason.ContentFilter => StopReason.Refusal,
            ChatFinishReason.Stop => calls.Count > 0 ? StopReason.ToolUse : StopReason.EndTurn, // some servers send "stop" with tool_calls
            null => StopReason.Error,
            _ => StopReason.EndTurn,
        };
        return new Stopped(reason, finish is null ? "stream ended without finish_reason" : null, msg);
    }
}
