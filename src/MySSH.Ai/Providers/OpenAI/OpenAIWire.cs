#pragma warning disable SCME0001 // JsonPatch (experimental): reasoning_content / max_tokens are not modelled by the SDK
using System.Text;
using System.Text.Json;
using OpenAI.Chat;

namespace MySSH.Ai.Providers.OpenAI;

/// <summary>
/// The assistant turn as stored in <see cref="AiMessage.RawContent"/>: the Chat Completions assistant message JSON
/// (<c>{"role":"assistant","content":...,"reasoning_content":...,"tool_calls":[...]}</c>), assembled from the stream.
/// </summary>
internal sealed record RawAssistant(string? Content, string? Reasoning, IReadOnlyList<RawToolCall> ToolCalls)
{
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("role", "assistant");
            if (string.IsNullOrEmpty(Content)) w.WriteNull("content");
            else w.WriteString("content", Content);
            if (!string.IsNullOrEmpty(Reasoning)) w.WriteString("reasoning_content", Reasoning);
            if (ToolCalls.Count > 0)
            {
                w.WriteStartArray("tool_calls");
                foreach (var c in ToolCalls)
                {
                    w.WriteStartObject();
                    w.WriteString("id", c.Id);
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", c.Name);
                    w.WriteString("arguments", c.Arguments);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Null when the stored JSON is not in this shape (corrupt / foreign): the caller then uses the normalized fields.</summary>
    public static RawAssistant? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var calls = new List<RawToolCall>();
            if (root.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    if (!tc.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) return null;
                    calls.Add(new RawToolCall(Str(tc, "id") ?? "", Str(fn, "name") ?? "", Str(fn, "arguments") ?? ""));
                }
            }
            return new RawAssistant(Str(root, "content"), Str(root, "reasoning_content"), calls);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

internal sealed record RawToolCall(string Id, string Name, string Arguments);

/// <summary>Builds the Chat Completions request from a <see cref="ChatRequest"/>.</summary>
internal static class OpenAIRequestBuilder
{
    const string NotExecuted = "ERROR: this tool call was not executed.";

    /// <param name="providerId">Raw content is rebuilt only for assistant turns with this <see cref="AiMessage.RawProvider"/>.</param>
    /// <param name="echoReasoning">Write <c>reasoning_content</c> back onto assistant turns of the current tool loop
    /// (DeepSeek thinking mode requires it there; earlier user turns' reasoning is not sent).</param>
    public static List<ChatMessage> BuildMessages(ChatRequest request, string providerId, bool echoReasoning)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt)) messages.Add(new SystemChatMessage(request.SystemPrompt));

        var history = request.History;
        int lastUser = -1;
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Role == AiRole.User) { lastUser = i; break; }

        // Chat Completions rejects an assistant tool_calls message that is not followed by a tool message per call id
        // (e.g. a turn cut off by max_tokens whose calls were never run): answer those with an error result.
        var pending = new List<string>();
        void AnswerPending()
        {
            foreach (var id in pending) messages.Add(new ToolChatMessage(id, NotExecuted));
            pending.Clear();
        }

        for (int i = 0; i < history.Count; i++)
        {
            var m = history[i];
            switch (m.Role)
            {
                case AiRole.User:
                    AnswerPending();
                    messages.Add(new UserChatMessage(m.Text ?? ""));
                    break;
                case AiRole.Assistant:
                {
                    AnswerPending();
                    var (msg, ids) = BuildAssistant(m, providerId, echoReasoning && i > lastUser);
                    messages.Add(msg);
                    pending.AddRange(ids);
                    break;
                }
                case AiRole.ToolResults:
                    // Chat Completions has no is_error flag: encode it in the content.
                    foreach (var r in m.ToolResults)
                    {
                        messages.Add(new ToolChatMessage(r.ToolCallId, r.IsError ? "ERROR: " + r.Content : r.Content));
                        pending.Remove(r.ToolCallId);
                    }
                    AnswerPending();
                    break;
            }
        }
        AnswerPending();
        return messages;
    }

    static (AssistantChatMessage Message, List<string> CallIds) BuildAssistant(AiMessage m, string providerId, bool echoReasoning)
    {
        RawAssistant? raw = m.RawProvider == providerId && m.RawContent is not null ? RawAssistant.TryParse(m.RawContent) : null;
        raw ??= new RawAssistant(m.Text, null,
            [.. m.ToolCalls.Select(c => new RawToolCall(c.Id, c.Name, c.RawInput.Length > 0 ? c.RawInput : c.Input?.GetRawText() ?? "{}"))]);

        AssistantChatMessage msg;
        if (raw.ToolCalls.Count > 0)
        {
            msg = new AssistantChatMessage(raw.ToolCalls.Select(c => ChatToolCall.CreateFunctionToolCall(
                c.Id, c.Name, BinaryData.FromString(string.IsNullOrWhiteSpace(c.Arguments) ? "{}" : c.Arguments))));
            if (!string.IsNullOrEmpty(raw.Content)) msg.Content.Add(ChatMessageContentPart.CreateTextPart(raw.Content));
        }
        else
        {
            msg = new AssistantChatMessage(raw.Content ?? "");
        }
        if (echoReasoning && !string.IsNullOrEmpty(raw.Reasoning)) msg.Patch.Set("$.reasoning_content"u8, raw.Reasoning);
        return (msg, [.. raw.ToolCalls.Select(c => c.Id)]);
    }

    /// <param name="maxCompletionTokens">OpenAI: <c>max_completion_tokens</c> (reasoning models reject <c>max_tokens</c>);
    /// DeepSeek and other compatible servers: the widely supported <c>max_tokens</c>.</param>
    public static ChatCompletionOptions BuildOptions(ChatRequest request, bool tools, bool maxCompletionTokens)
    {
        var options = new ChatCompletionOptions();
        if (request.MaxTokens > 0)
        {
            if (maxCompletionTokens) options.MaxOutputTokenCount = request.MaxTokens;
            else options.Patch.Set("$.max_tokens"u8, request.MaxTokens);
        }
        if (tools && request.Tools.Count > 0)
        {
            foreach (var t in request.Tools)
            {
                var schema = t.InputSchema.ValueKind == JsonValueKind.Object ? t.InputSchema.GetRawText() : """{"type":"object","properties":{}}""";
                // strict stays unset: many compatible servers reject or mis-handle it.
                options.Tools.Add(ChatTool.CreateFunctionTool(t.Name, t.Description, BinaryData.FromString(schema)));
            }
            options.ToolChoice = ChatToolChoice.CreateAutoChoice();
            options.AllowParallelToolCalls = true;
        }
        return options;
    }
}

/// <summary>
/// Assembles streamed tool_call fragments. Fragments are keyed by <c>index</c> and may interleave between calls; a fragment
/// that reuses an index with a different id (servers that always send index 0) starts a new call.
/// </summary>
internal sealed class ToolCallAssembler
{
    sealed class Slot
    {
        public string? Id;
        public string? Name;
        public readonly StringBuilder Args = new();
        public bool Started;
        public string EventId = "";
    }

    readonly List<Slot> _slots = [];
    readonly Dictionary<int, Slot> _byIndex = [];

    public int Count => _slots.Count;

    public void Add(int index, string? id, string? name, string? args, List<ChatEvent> events)
    {
        if (!_byIndex.TryGetValue(index, out var slot) || (!string.IsNullOrEmpty(id) && slot.Id is not null && slot.Id != id))
        {
            slot = new Slot();
            _slots.Add(slot);
            _byIndex[index] = slot;
        }
        if (!string.IsNullOrEmpty(id)) slot.Id ??= id;
        if (!string.IsNullOrEmpty(name)) slot.Name ??= name;
        if (!string.IsNullOrEmpty(args)) slot.Args.Append(args);

        if (!slot.Started && slot.Name is not null)
        {
            slot.Started = true;
            slot.EventId = slot.Id ?? $"call_{_slots.IndexOf(slot)}";
            events.Add(new ToolCallStarted(slot.EventId, slot.Name));
            if (slot.Args.Length > 0) events.Add(new ToolCallInputDelta(slot.EventId, slot.Args.ToString()));
        }
        else if (slot.Started && !string.IsNullOrEmpty(args))
        {
            events.Add(new ToolCallInputDelta(slot.EventId, args));
        }
    }

    public List<ToolCall> Build()
    {
        var calls = new List<ToolCall>(_slots.Count);
        for (int i = 0; i < _slots.Count; i++)
        {
            var s = _slots[i];
            var id = s.Started ? s.EventId : s.Id ?? $"call_{i}";
            calls.Add(s.Name is null
                ? new ToolCall(id, "", null, s.Args.ToString(), "The tool call has no function name.")
                : ToolInputParser.Parse(id, s.Name, s.Args.ToString()));
        }
        return calls;
    }
}

internal static class ToolInputParser
{
    /// <summary>Strict JSON (no comments, no trailing commas) that must be an object; empty arguments mean <c>{}</c>.</summary>
    public static ToolCall Parse(string id, string name, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? new ToolCall(id, name, doc.RootElement.Clone(), raw)
                : new ToolCall(id, name, null, raw, "The tool arguments are not a JSON object.");
        }
        catch (JsonException ex)
        {
            return new ToolCall(id, name, null, raw, "The tool arguments are not valid JSON: " + ex.Message);
        }
    }
}
