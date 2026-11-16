using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MySSH.Ai.Providers.Anthropic;

/// <summary>
/// Turns the Messages API stream into normalized <see cref="ChatEvent"/>s and rebuilds the assistant content array
/// (what the non-streaming API would have returned) for <see cref="AiMessage.RawContent"/>:
/// <list type="bullet">
/// <item>blocks start as their <c>content_block_start</c> object; text_delta / thinking_delta / signature_delta /
/// input_json_delta / citations_delta are applied; unknown delta fields are merged generically (strings appended,
/// other values set); unknown block types are kept as received;</item>
/// <item>tool input is parsed strictly at <c>content_block_stop</c>; invalid JSON → <see cref="ToolCall.InputError"/> and
/// the block keeps the <c>input</c> of its start event (normally <c>{}</c>), as in the spike;</item>
/// <item>null-valued block fields are dropped (requests never carry JSON nulls) and a thinking block that never got a
/// signature (cut off) is left out, since the API rejects it on replay.</item>
/// </list>
/// Malformed input throws <see cref="InvalidDataException"/>.
/// </summary>
internal sealed class AnthropicStreamAssembler
{
    sealed class Block(int index, JsonObject node)
    {
        public int Index { get; } = index;
        public JsonObject Node { get; } = node;
        public string Type => Node["type"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : "";
        public StringBuilder? Text { get; set; }
        public StringBuilder? Thinking { get; set; }
        public StringBuilder? Json { get; set; }
        public bool Stopped { get; set; }
        public ToolCall? Call { get; set; }
        public bool EmittedThinking { get; set; }
    }

    readonly SortedDictionary<int, Block> _blocks = [];
    string? _model;
    string? _fallbackModel;
    long _inputTokens, _outputTokens, _cacheRead, _cacheWrite;
    string? _stopReason;
    string? _stopDetail;
    bool _thinkingTextSeen;

    public bool Started { get; private set; }
    public bool Completed { get; private set; }

    /// <summary>Handles one SSE event. Returns a <see cref="Stopped"/> for an <c>error</c> event, else null.</summary>
    public Stopped? Feed(SseEvent ev, List<ChatEvent> output)
    {
        if (ev.Data.Length == 0 || ev.Data == "[DONE]") return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(ev.Data); }
        catch (JsonException ex) { throw new InvalidDataException($"Malformed stream event ({ev.Event ?? "data"}): {ex.Message}", ex); }
        using (doc)
        {
            var data = doc.RootElement;
            if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Malformed stream event: not a JSON object.");
            var type = data.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : ev.Event;
            switch (type)
            {
                case "message_start":
                    OnMessageStart(Require(data, "message"));
                    break;
                case "content_block_start":
                    OnBlockStart(Index(data), Require(data, "content_block"), output);
                    break;
                case "content_block_delta":
                    OnBlockDelta(Index(data), Require(data, "delta"), output);
                    break;
                case "content_block_stop":
                    OnBlockStop(Index(data), output);
                    break;
                case "message_delta":
                    OnMessageDelta(data, output);
                    break;
                case "message_stop":
                    Completed = true;
                    break;
                case "error":
                    return ErrorEvent(data);
                default:
                    break; // ping and future event types
            }
        }
        return null;
    }

    /// <summary>A complete (non-streamed) message, for endpoints that answer <c>stream:true</c> with plain JSON.</summary>
    public void FeedMessage(JsonElement message, List<ChatEvent> output)
    {
        if (message.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Malformed response: not a JSON object.");
        if (message.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "error")
            throw new InvalidDataException("Unexpected error object in a successful response.");
        OnMessageStart(message);
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var block in content.EnumerateArray())
            {
                OnBlockStart(i, block, output);
                OnBlockStop(i, output);
                i++;
            }
        }
        OnMessageDeltaCore(message, message.TryGetProperty("usage", out var u) ? u : default, output);
        Completed = true;
    }

    static JsonElement Require(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : throw new InvalidDataException($"Malformed stream event: missing '{name}'.");

    static int Index(JsonElement data) =>
        data.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n) && n >= 0
            ? n : throw new InvalidDataException("Malformed stream event: missing 'index'.");

    // ------------------------------------------------------------------ events

    void OnMessageStart(JsonElement message)
    {
        if (Started) return;
        Started = true;
        if (message.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) _model = m.GetString();
        if (message.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object) ReadUsage(u);
    }

    void OnBlockStart(int index, JsonElement block, List<ChatEvent> output)
    {
        RequireStarted();
        if (block.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Malformed content_block_start.");
        if (_blocks.ContainsKey(index)) throw new InvalidDataException($"Duplicate content block {index}.");
        var node = JsonNode.Parse(block.GetRawText()) as JsonObject ?? throw new InvalidDataException("Malformed content_block_start.");
        var b = new Block(index, node);
        _blocks[index] = b;

        switch (b.Type)
        {
            case "text":
                if (StringOf(node, "text") is { Length: > 0 } text) output.Add(new TextDelta(text));
                break;
            case "thinking":
                if (StringOf(node, "thinking") is { Length: > 0 } thinking) EmitThinking(b, thinking, output);
                break;
            case "tool_use":
                output.Add(new ToolCallStarted(StringOf(node, "id") ?? "", StringOf(node, "name") ?? ""));
                break;
            case "fallback":
                var from = node["from"] is JsonObject f ? StringOf(f, "model") : null;
                var to = node["to"] is JsonObject tt ? StringOf(tt, "model") : null;
                if (to is not null) _fallbackModel = to;
                output.Add(new FallbackOccurred(from ?? _model ?? "", to ?? ""));
                break;
        }
    }

    void OnBlockDelta(int index, JsonElement delta, List<ChatEvent> output)
    {
        if (!_blocks.TryGetValue(index, out var b) || b.Stopped)
            throw new InvalidDataException($"Delta for unknown or closed content block {index}.");
        var dtype = delta.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        switch (dtype)
        {
            case "text_delta":
            {
                var s = StringProp(delta, "text");
                (b.Text ??= new StringBuilder(StringOf(b.Node, "text") ?? "")).Append(s);
                if (s.Length > 0) output.Add(new TextDelta(s));
                break;
            }
            case "thinking_delta":
            {
                var s = StringProp(delta, "thinking");
                (b.Thinking ??= new StringBuilder(StringOf(b.Node, "thinking") ?? "")).Append(s);
                if (s.Length > 0) EmitThinking(b, s, output);
                break;
            }
            case "signature_delta":
                b.Node["signature"] = StringProp(delta, "signature");
                break;
            case "input_json_delta":
            {
                var s = StringProp(delta, "partial_json");
                (b.Json ??= new StringBuilder()).Append(s);
                if (b.Type == "tool_use" && s.Length > 0) output.Add(new ToolCallInputDelta(StringOf(b.Node, "id") ?? "", s));
                break;
            }
            case "citations_delta":
                if (delta.TryGetProperty("citation", out var citation))
                {
                    if (b.Node["citations"] is not JsonArray list) b.Node["citations"] = list = [];
                    list.Add(JsonNode.Parse(citation.GetRawText()));
                }
                break;
            default:
                // Unknown delta: merge generically so the block still round-trips.
                foreach (var p in delta.EnumerateObject())
                {
                    if (p.NameEquals("type")) continue;
                    if (p.Value.ValueKind == JsonValueKind.String && (b.Node[p.Name] is null || b.Node[p.Name] is JsonValue { } ev && ev.TryGetValue<string>(out _)))
                        b.Node[p.Name] = (StringOf(b.Node, p.Name) ?? "") + p.Value.GetString();
                    else
                        b.Node[p.Name] = JsonNode.Parse(p.Value.GetRawText());
                }
                break;
        }
    }

    void EmitThinking(Block b, string text, List<ChatEvent> output)
    {
        // Separate the notes / summaries of different thinking blocks, as Stopped.Message.Thinking does.
        if (!b.EmittedThinking && _thinkingTextSeen) output.Add(new ThinkingDelta("\n\n"));
        b.EmittedThinking = true;
        _thinkingTextSeen = true;
        output.Add(new ThinkingDelta(text));
    }

    void OnBlockStop(int index, List<ChatEvent> output)
    {
        if (!_blocks.TryGetValue(index, out var b))
            throw new InvalidDataException($"Stop for unknown content block {index}.");
        if (b.Stopped) return;
        Close(b, output);
    }

    static void Close(Block b, List<ChatEvent> output)
    {
        b.Stopped = true;
        if (b.Text is not null) b.Node["text"] = b.Text.ToString();
        if (b.Thinking is not null) b.Node["thinking"] = b.Thinking.ToString();

        string? streamed = b.Json?.ToString();
        if (b.Type == "tool_use")
        {
            b.Call = BuildCall(b, streamed);
            output.Add(new ToolCallCompleted(b.Call));
        }
        else if (!string.IsNullOrWhiteSpace(streamed))
        {
            // e.g. server_tool_use: same accumulation, no client-side call.
            try { b.Node["input"] = JsonNode.Parse(streamed); }
            catch (JsonException) { }
        }
    }

    static ToolCall BuildCall(Block b, string? streamed)
    {
        var id = StringOf(b.Node, "id") ?? "";
        var name = StringOf(b.Node, "name") ?? "";
        var raw = string.IsNullOrWhiteSpace(streamed) ? (b.Node["input"]?.ToJsonString() ?? "{}") : streamed;
        try
        {
            // Strict: no comments, no trailing commas, one value.
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new ToolCall(id, name, null, raw, $"Tool input must be a JSON object, got {doc.RootElement.ValueKind}.");
            b.Node["input"] = JsonNode.Parse(raw);
            return new ToolCall(id, name, doc.RootElement.Clone(), raw);
        }
        catch (JsonException ex)
        {
            if (b.Node["input"] is not JsonObject) b.Node["input"] = new JsonObject();
            return new ToolCall(id, name, null, raw, "Tool input is not valid JSON: " + ex.Message);
        }
    }

    void OnMessageDelta(JsonElement data, List<ChatEvent> output)
    {
        RequireStarted();
        var delta = data.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        OnMessageDeltaCore(delta, data.TryGetProperty("usage", out var u) ? u : default, output);
    }

    void OnMessageDeltaCore(JsonElement delta, JsonElement usage, List<ChatEvent> output)
    {
        if (delta.ValueKind == JsonValueKind.Object)
        {
            if (delta.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String) _stopReason = sr.GetString();
            if (delta.TryGetProperty("stop_details", out var sd) && sd.ValueKind == JsonValueKind.Object) _stopDetail = FormatStopDetails(sd);
        }
        if (usage.ValueKind == JsonValueKind.Object) ReadUsage(usage);
        output.Add(new Usage(_inputTokens, _outputTokens, _cacheRead, _cacheWrite));
    }

    static string? FormatStopDetails(JsonElement sd)
    {
        var category = sd.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var explanation = sd.TryGetProperty("explanation", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        if (category is null) return string.IsNullOrEmpty(explanation) ? null : explanation;
        return string.IsNullOrEmpty(explanation) ? category : $"{category}: {explanation}";
    }

    void ReadUsage(JsonElement u)
    {
        static bool Get(JsonElement u, string name, out long v)
        {
            v = 0;
            return u.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out v);
        }
        if (Get(u, "input_tokens", out var i)) _inputTokens = i;
        if (Get(u, "output_tokens", out var o)) _outputTokens = o;
        if (Get(u, "cache_read_input_tokens", out var r)) _cacheRead = r;
        if (Get(u, "cache_creation_input_tokens", out var w)) _cacheWrite = w;
    }

    static Stopped ErrorEvent(JsonElement data)
    {
        string? type = null, message = null;
        if (data.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) type = t.GetString();
            if (e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
        }
        var requestId = data.TryGetProperty("request_id", out var rid) && rid.ValueKind == JsonValueKind.String ? rid.GetString() : null;
        var kind = AnthropicErrors.KindFromErrorType(type);
        var detail = $"Stream error {type ?? "unknown"}: {message ?? "(no message)"}" + (requestId is null ? "" : $" (request-id: {requestId})");
        return new Stopped(StopReason.Error, detail, null, kind);
    }

    void RequireStarted()
    {
        if (!Started) throw new InvalidDataException("Malformed stream: event before message_start.");
    }

    // ------------------------------------------------------------------ result

    /// <summary>The final event once the stream ended normally (message_delta seen).</summary>
    public Stopped Finish(List<ChatEvent> output)
    {
        if (!Started) throw new InvalidDataException("The stream ended before message_start.");
        foreach (var b in _blocks.Values)
            if (!b.Stopped) Close(b, output);
        if (_stopReason is null) throw new InvalidDataException("The stream ended before the message was complete (no stop_reason).");

        if (_stopReason == "refusal")
            // Partial output (and any tool call in it) is discarded, never appended to history.
            return new Stopped(StopReason.Refusal, _stopDetail, null);

        var blocks = _blocks.Values.ToList();
        var boundary = -1;
        for (var i = blocks.Count - 1; i >= 0; i--)
            if (blocks[i].Type == "fallback") { boundary = i; break; }

        var text = new StringBuilder();
        var thinking = new List<string>();
        var calls = new List<ToolCall>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            switch (b.Type)
            {
                case "text":
                    text.Append(StringOf(b.Node, "text"));
                    break;
                case "thinking":
                    if (StringOf(b.Node, "thinking") is { Length: > 0 } th) thinking.Add(th);
                    break;
                case "tool_use" when i > boundary && b.Call is not null:
                    // Calls before the last fallback block belong to the model that declined: never executed.
                    calls.Add(b.Call);
                    break;
            }
        }

        var reason = _stopReason switch
        {
            "end_turn" or "stop_sequence" => calls.Count > 0 ? StopReason.ToolUse : StopReason.EndTurn,
            "tool_use" => calls.Count > 0 ? StopReason.ToolUse : StopReason.EndTurn,
            "max_tokens" or "model_context_window_exceeded" => StopReason.MaxTokens,
            // pause_turn (server-side tool loop paused) and future reasons: the turn is kept as is; the agent loop can
            // continue by sending the history back. Detail names the raw reason.
            _ => calls.Count > 0 ? StopReason.ToolUse : StopReason.EndTurn,
        };
        var detail = _stopReason is "end_turn" or "tool_use" or "stop_sequence" or "max_tokens" ? _stopDetail : _stopReason;

        var message = new AiMessage
        {
            Role = AiRole.Assistant,
            Text = text.Length == 0 ? null : text.ToString(),
            ToolCalls = calls,
            Thinking = thinking.Count == 0 ? null : string.Join("\n\n", thinking),
            RawProvider = AnthropicProvider.Id,
            RawContent = RawContent(blocks),
            ServedByModel = _fallbackModel ?? _model,
        };
        return new Stopped(reason, detail, message);
    }

    static string RawContent(List<Block> blocks)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer, AnthropicRequestBuilder.JsonOptions))
        {
            w.WriteStartArray();
            foreach (var b in blocks)
            {
                if (b.Type == "thinking" && string.IsNullOrEmpty(StringOf(b.Node, "signature")))
                    continue; // cut off before its signature: not replayable
                foreach (var (k, v) in b.Node.ToList())
                    if (v is null) b.Node.Remove(k);
                b.Node.WriteTo(w);
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ------------------------------------------------------------------ helpers

    static string? StringOf(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static string StringProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
