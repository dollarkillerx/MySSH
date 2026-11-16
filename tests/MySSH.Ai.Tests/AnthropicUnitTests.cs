using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MySSH.Ai.Providers.Anthropic;

namespace MySSH.Ai.Tests;

/// <summary>SSE parser, strict-schema conversion and fallback echo rules in isolation.</summary>
public sealed class AnthropicUnitTests
{
    static async Task<List<SseEvent>> Parse(string text, bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        using var ms = new MemoryStream(bytes);
        var list = new List<SseEvent>();
        await foreach (var e in SseReader.ReadAsync(ms, TestContext.Current.CancellationToken)) list.Add(e);
        return list;
    }

    // ------------------------------------------------------------------ SSE

    [Fact]
    public async Task Sse_handles_crlf_cr_comments_multiline_data_and_field_forms()
    {
        var text =
            ": keep-alive comment\r\n" +
            "event: message_start\r\n" +
            "data: {\"a\":1}\r\n" +
            "\r\n" +
            "event: ping\r" +
            "data:{\"type\": \"ping\"}\r" +
            "\r" +
            "id: 7\n" +
            "retry: 1000\n" +
            "unknown-field: x\n" +
            "data: line1\n" +
            "data:  line2 (one space stripped)\n" +
            "data\n" +
            "\n" +
            "event: only-name-no-data\n" +
            "\n" +
            "\n\n" +
            "event: last\n" +
            "data: no trailing blank line";
        var events = await Parse(text, bom: true);
        Assert.Equal(
            [
                new SseEvent("message_start", "{\"a\":1}"),
                new SseEvent("ping", "{\"type\": \"ping\"}"),
                new SseEvent(null, "line1\n line2 (one space stripped)\n"),
                new SseEvent("last", "no trailing blank line"),
            ], events);
    }

    [Fact]
    public async Task Sse_event_name_does_not_leak_into_the_next_event()
    {
        var events = await Parse("event: a\ndata: 1\n\ndata: 2\n\n");
        Assert.Equal([new SseEvent("a", "1"), new SseEvent(null, "2")], events);
    }

    // ------------------------------------------------------------------ strict schema

    [Fact]
    public void Strict_schema_strips_unsupported_keywords_into_descriptions()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{
              "n":{"type":"integer","description":"Count.","minimum":1,"maximum":2000,"default":100},
              "s":{"type":"string","minLength":1,"maxLength":256,"pattern":"^a+$","format":"hostname"},
              "f":{"type":"string","description":"Odd format.","format":"ipv5"},
              "list":{"type":"array","items":{"type":"object","properties":{"x":{"type":"number","exclusiveMinimum":0,"multipleOf":0.5}}},
                      "minItems":1,"maxItems":5,"uniqueItems":true},
              "m":{"type":"string","maxLength":10,"enum":["a","b"]},
              "u":{"anyOf":[{"type":"object","properties":{}},{"type":"null"}]}
            },"required":["n"],"additionalProperties":true}
            """).RootElement;

        var converted = StrictSchema.ToJson(schema);
        Assert.Equal(
            """
            {"type":"object","properties":{"n":{"type":"integer","description":"Count. (1–2000; default 100)"},"s":{"type":"string","format":"hostname","description":"(1–256 characters; pattern ^a+$)"},"f":{"type":"string","description":"Odd format. (format ipv5)"},"list":{"type":"array","items":{"type":"object","properties":{"x":{"type":"number","description":"(> 0; multiple of 0.5)"}},"additionalProperties":false},"description":"(1–5 items; unique items)"},"m":{"type":"string","enum":["a","b"],"description":"(at most 10 characters)"},"u":{"anyOf":[{"type":"object","properties":{},"additionalProperties":false},{"type":"null"}]}},"required":["n"],"additionalProperties":false}
            """,
            converted);

        // Deterministic and the input is left untouched.
        Assert.Equal(converted, StrictSchema.ToJson(schema));
        Assert.Equal(2000, schema.GetProperty("properties").GetProperty("n").GetProperty("maximum").GetInt32());
    }

    [Fact]
    public void Every_catalog_schema_converts_to_strict_compatible_form()
    {
        string[] banned = ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength",
            "pattern", "minItems", "maxItems", "uniqueItems", "default"];
        foreach (var tool in AnthropicProviderTests.CatalogTools)
        {
            var node = StrictSchema.Convert(tool.InputSchema);
            Walk(node, tool.Name);
        }

        void Walk(JsonNode? n, string path)
        {
            if (n is JsonObject o)
            {
                if (o.ContainsKey("properties") || (o["type"] is JsonValue t && t.GetValue<string>() == "object"))
                    Assert.False(o["additionalProperties"]!.GetValue<bool>(), path);
                foreach (var (k, v) in o)
                {
                    if (k != "properties") Assert.DoesNotContain(k, banned);
                    Walk(v, path + "." + k);
                }
            }
            else if (n is JsonArray a)
            {
                foreach (var v in a) Walk(v, path + "[]");
            }
        }
    }

    // ------------------------------------------------------------------ fallback echo rules

    [Fact]
    public void Replay_without_fallback_returns_the_stored_string_itself()
    {
        var raw = """[ {"type":"thinking","thinking":"t","signature":"s"} ,{"type":"text","text":"x"}]""";
        Assert.Same(raw, AnthropicRequestBuilder.ReplayContent(raw));
        Assert.Equal("", AnthropicRequestBuilder.ReplayContent("[]"));
        Assert.Null(AnthropicRequestBuilder.ReplayContent("{not json"));
        Assert.Null(AnthropicRequestBuilder.ReplayContent("""{"type":"text"}"""));
    }

    [Fact]
    public void Replay_after_fallback_keeps_text_paired_server_tools_and_the_boundary_blocks()
    {
        var raw = "[" + string.Join(",",
            """{"type":"thinking","thinking":"a","signature":"s1"}""",          // 0 drop
            """{"type":"redacted_thinking","data":"zzz"}""",                     // 1 drop
            """{"type":"text","text":"partial"}""",                              // 2 keep
            """{"type":"server_tool_use","id":"srv_1","name":"web_search","input":{}}""", // 3 keep (paired)
            """{"type":"web_search_tool_result","tool_use_id":"srv_1","content":[]}""",   // 4 keep
            """{"type":"server_tool_use","id":"srv_2","name":"web_search","input":{}}""", // 5 drop (unpaired)
            """{"type":"tool_use","id":"toolu_1","name":"x","input":{}}""",      // 6 drop
            """{"type":"fallback","from":{"model":"a"},"to":{"model":"b"}}""",   // 7 keep (earlier boundary)
            """{"type":"mystery","v":1}""",                                      // 8 drop (unknown)
            """{"type":"fallback","from":{"model":"b"},"to":{"model":"c"}}""",   // 9 last boundary
            """{"type":"thinking","thinking":"after","signature":"s2"}""",      // 10 keep
            """{"type":"tool_use","id":"toolu_2","name":"x","input":{"k":1}}""") + "]"; // 11 keep
        using var doc = JsonDocument.Parse(raw);
        var blocks = doc.RootElement.EnumerateArray().Select(b => b.GetRawText()).ToList();
        var expected = "[" + string.Join(",", new[] { 2, 3, 4, 7, 9, 10, 11 }.Select(i => blocks[i])) + "]";
        Assert.Equal(expected, AnthropicRequestBuilder.ReplayContent(raw));
    }
}
