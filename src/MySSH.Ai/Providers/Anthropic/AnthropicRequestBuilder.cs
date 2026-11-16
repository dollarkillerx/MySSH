using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MySSH.Ai.Providers.Anthropic;

/// <summary>
/// Builds the Messages API request (design §7.2) with <see cref="Utf8JsonWriter"/> only (NativeAOT-safe, no reflection).
/// Optional fields are omitted rather than written as null. The output is deterministic for the same input, so the
/// prompt prefix (tools → system → messages) stays byte-identical across the turns of a conversation.
/// </summary>
internal static class AnthropicRequestBuilder
{
    public const string ApiVersion = "2026-10-21";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";
    public const string ThinkingDisplayUpdatesBeta = "thinking-display-updates-2026-08-18";
    public const string ThinkingBindingBeta = "thinking-binding-controls-2026-08-01";

    /// <summary>The drop_block safety net is for the real API; Anthropic-compatible gateways may reject unknown betas / fields.</summary>
    internal static bool SendsBlockBinding(AiProviderConfig config) => config.Kind != AiProviderKind.Custom;

    static readonly JsonWriterOptions WriterOptions = new()
    {
        // Not HTML: no need to escape <, >, &, ' or non-ASCII. Still valid JSON for any input.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "x-api-key", "content-type", "content-length", "host", "transfer-encoding", "anthropic-beta",
    };

    // ------------------------------------------------------------------ headers

    public static List<string> Betas(AiProviderConfig config)
    {
        var betas = new List<string>();
        if (config.ServerSideFallback) betas.Add(FallbackBeta);
        if (config.ThinkingDisplay == AiThinkingDisplay.Updates) betas.Add(ThinkingDisplayUpdatesBeta);
        if (SendsBlockBinding(config)) betas.Add(ThinkingBindingBeta);
        return betas;
    }

    /// <summary>
    /// anthropic-version, the key (x-api-key or Bearer; none when the key is empty), the configured extra headers (never
    /// auth / framing headers; an extra anthropic-version replaces ours) and anthropic-beta (ours + any extra ones).
    /// Nothing comes from the environment.
    /// </summary>
    public static void ApplyHeaders(HttpRequestMessage msg, AiProviderConfig config, string? apiKey, IReadOnlyList<string> betas)
    {
        var h = msg.Headers;
        var extraVersion = config.ExtraHeaders.FirstOrDefault(kv => kv.Key.Equals("anthropic-version", StringComparison.OrdinalIgnoreCase));
        h.TryAddWithoutValidation("anthropic-version", extraVersion.Key is null ? ApiVersion : extraVersion.Value);

        if (!string.IsNullOrEmpty(apiKey))
        {
            if (config.UseBearerAuth) h.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            else h.TryAddWithoutValidation("x-api-key", apiKey);
        }

        var allBetas = new List<string>(betas);
        foreach (var (name, value) in config.ExtraHeaders)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (name.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var b in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!allBetas.Contains(b, StringComparer.Ordinal)) allBetas.Add(b);
                continue;
            }
            if (ReservedHeaders.Contains(name) || name.Equals("anthropic-version", StringComparison.OrdinalIgnoreCase))
                continue;
            h.TryAddWithoutValidation(name, value);
        }
        if (allBetas.Count > 0)
            h.TryAddWithoutValidation("anthropic-beta", string.Join(",", allBetas));
    }

    // ------------------------------------------------------------------ body

    public static byte[] BuildMessagesBody(AiProviderConfig config, ChatRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("model", request.Model);
            w.WriteNumber("max_tokens", request.MaxTokens);
            w.WriteBoolean("stream", true);

            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                w.WriteStartArray("system");
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", request.SystemPrompt);
                WriteEphemeral(w);
                w.WriteEndObject();
                w.WriteEndArray();
            }

            var tools = request.Tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            if (tools.Count > 0)
            {
                w.WriteStartArray("tools");
                for (var i = 0; i < tools.Count; i++)
                {
                    var t = tools[i];
                    w.WriteStartObject();
                    w.WriteString("name", t.Name);
                    w.WriteString("description", t.Description);
                    w.WritePropertyName("input_schema");
                    StrictSchema.Convert(t.InputSchema).WriteTo(w);
                    w.WriteBoolean("strict", true);
                    if (config.EagerInputStreaming) w.WriteBoolean("eager_input_streaming", true);
                    if (i == tools.Count - 1) WriteEphemeral(w);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartObject("tool_choice");
                w.WriteString("type", "auto");
                w.WriteEndObject();
            }

            w.WriteStartObject("thinking");
            w.WriteString("type", "adaptive");
            w.WriteString("display", config.ThinkingDisplay switch
            {
                AiThinkingDisplay.Updates => "updates",
                AiThinkingDisplay.Omitted => "omitted",
                _ => "summarized",
            });
            if (SendsBlockBinding(config))
            {
                w.WriteStartObject("block_binding");
                w.WriteString("prefix_mismatch_behavior", "drop_block");
                w.WriteEndObject();
            }
            w.WriteEndObject();

            w.WriteStartObject("output_config");
            w.WriteString("effort", request.Effort switch
            {
                AiEffort.Low => "low",
                AiEffort.High => "high",
                _ => "medium",
            });
            w.WriteEndObject();

            if (config.ServerSideFallback)
                w.WriteString("fallbacks", "default");

            // Automatic breakpoint on the last cacheable block: caches the growing conversation (system + last tool are
            // the other two explicit breakpoints; 3 of the 4 allowed).
            WriteEphemeral(w);

            w.WriteStartArray("messages");
            WriteMessages(w, request.History);
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    static void WriteEphemeral(Utf8JsonWriter w)
    {
        w.WriteStartObject("cache_control");
        w.WriteString("type", "ephemeral");
        w.WriteEndObject();
    }

    internal static void WriteMessages(Utf8JsonWriter w, IReadOnlyList<AiMessage> history)
    {
        foreach (var m in history)
        {
            switch (m.Role)
            {
                case AiRole.User:
                    w.WriteStartObject();
                    w.WriteString("role", "user");
                    w.WriteStartArray("content");
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("text", m.Text ?? "");
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteEndObject();
                    break;

                case AiRole.ToolResults:
                    if (m.ToolResults.Count == 0) break;
                    w.WriteStartObject();
                    w.WriteString("role", "user");
                    w.WriteStartArray("content");
                    foreach (var r in m.ToolResults)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "tool_result");
                        w.WriteString("tool_use_id", r.ToolCallId);
                        w.WriteString("content", r.Content);
                        if (r.IsError) w.WriteBoolean("is_error", true);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                    break;

                case AiRole.Assistant:
                    WriteAssistant(w, m);
                    break;
            }
        }
    }

    static void WriteAssistant(Utf8JsonWriter w, AiMessage m)
    {
        if (m.RawProvider == AnthropicProvider.Id && m.RawContent is { } raw && ReplayContent(raw) is { } content)
        {
            if (content.Length == 0) return; // empty array: an empty assistant turn is a 400 and carries nothing to replay
            w.WriteStartObject();
            w.WriteString("role", "assistant");
            w.WritePropertyName("content");
            w.WriteRawValue(content, skipInputValidation: true); // validated by ReplayContent
            w.WriteEndObject();
            return;
        }

        // Another provider's turn (or unreadable raw content): normalized text + tool_use, no thinking.
        if (string.IsNullOrEmpty(m.Text) && m.ToolCalls.Count == 0) return;
        w.WriteStartObject();
        w.WriteString("role", "assistant");
        w.WriteStartArray("content");
        if (!string.IsNullOrEmpty(m.Text))
        {
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", m.Text);
            w.WriteEndObject();
        }
        foreach (var c in m.ToolCalls)
        {
            w.WriteStartObject();
            w.WriteString("type", "tool_use");
            w.WriteString("id", c.Id);
            w.WriteString("name", c.Name);
            w.WritePropertyName("input");
            WriteToolInput(w, c);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void WriteToolInput(Utf8JsonWriter w, ToolCall c)
    {
        if (c.Input is { ValueKind: JsonValueKind.Object } input)
        {
            input.WriteTo(w);
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(c.RawInput);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                doc.RootElement.WriteTo(w);
                return;
            }
        }
        catch (JsonException) { }
        w.WriteStartObject();
        w.WriteEndObject();
    }

    // ------------------------------------------------------------------ replay of stored Claude content

    /// <summary>
    /// The content array to send back for a stored Claude turn. Without a <c>fallback</c> block it is
    /// <paramref name="raw"/> itself, byte for byte (preserved thinking and prompt caching need the exact bytes). With
    /// one, the documented echo rule applies: before the LAST fallback block, thinking / redacted_thinking / tool_use,
    /// unpaired server_tool_use and unknown block types are omitted; text, paired server-tool blocks, the fallback blocks
    /// (kept in place) and everything after the boundary are echoed, each element's bytes unchanged. Deterministic, so
    /// later requests repeat the same bytes. Empty for an empty array; null when <paramref name="raw"/> is not a JSON array.
    /// </summary>
    internal static string? ReplayContent(string raw)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return null;
            var blocks = root.EnumerateArray().ToList();
            if (blocks.Count == 0) return "";
            var boundary = LastFallbackIndex(blocks);
            if (boundary < 0) return raw;

            var keep = KeptBeforeBoundary(blocks, boundary);
            var buffer = new ArrayBufferWriter<byte>(raw.Length);
            using (var w = new Utf8JsonWriter(buffer, WriterOptions))
            {
                w.WriteStartArray();
                for (var i = 0; i < blocks.Count; i++)
                    if (i >= boundary || keep[i])
                        w.WriteRawValue(blocks[i].GetRawText(), skipInputValidation: true);
                w.WriteEndArray();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    internal static string? BlockType(JsonElement block) =>
        block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;

    internal static int LastFallbackIndex(List<JsonElement> blocks)
    {
        for (var i = blocks.Count - 1; i >= 0; i--)
            if (BlockType(blocks[i]) == "fallback") return i;
        return -1;
    }

    /// <summary>Which blocks before <paramref name="boundary"/> are echoed (see <see cref="ReplayContent"/>).</summary>
    static bool[] KeptBeforeBoundary(List<JsonElement> blocks, int boundary)
    {
        var keep = new bool[blocks.Count];
        var resultIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < boundary; i++)
            if (BlockType(blocks[i]) is { } t && t.EndsWith("_tool_result", StringComparison.Ordinal) && StringProp(blocks[i], "tool_use_id") is { } rid)
                resultIds.Add(rid);
        var pairedServerUses = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < boundary; i++)
        {
            switch (BlockType(blocks[i]))
            {
                case "text":
                case "fallback":
                    keep[i] = true;
                    break;
                case "server_tool_use" when StringProp(blocks[i], "id") is { } id && resultIds.Contains(id):
                    keep[i] = true;
                    pairedServerUses.Add(id);
                    break;
            }
        }
        for (var i = 0; i < boundary; i++)
            if (BlockType(blocks[i]) is { } t && t.EndsWith("_tool_result", StringComparison.Ordinal)
                && StringProp(blocks[i], "tool_use_id") is { } rid && pairedServerUses.Contains(rid))
                keep[i] = true;
        return keep;
    }

    static string? StringProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static JsonWriterOptions JsonOptions => WriterOptions;
}
