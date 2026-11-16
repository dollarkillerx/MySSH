using System.Text.Json;
using Microsoft.Data.Sqlite;
using MySSH.Ai.Providers.Anthropic;
using MySSH.Tools;

namespace MySSH.Ai.Tests;

/// <summary>Claude provider against the local mock: request shape, stream normalization, replay, errors, models, probe.</summary>
public sealed class AnthropicProviderTests : IAsyncLifetime
{
    MockServer _mock = null!;
    readonly HttpClient _http = new();
    readonly List<TimeSpan> _delays = [];

    public async ValueTask InitializeAsync() => _mock = await MockServer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _mock.DisposeAsync();
        _http.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    internal static readonly IReadOnlyList<AiToolSpec> CatalogTools =
        new ToolCatalog().Tools.Select(d => new AiToolSpec(d.Name, d.Description, d.InputSchema)).Reverse().ToList(); // reversed: provider must sort

    AnthropicProvider Provider(Func<AiProviderConfig, AiProviderConfig>? edit = null, string? key = "test-key", string? baseUrl = null)
    {
        var cfg = AiProviderConfig.Claude() with { BaseUrl = baseUrl ?? _mock.BaseUrl };
        if (edit is not null) cfg = edit(cfg);
        var p = Assert.IsType<AnthropicProvider>(AiProviders.Create(cfg, key, _http));
        p.Delay = (d, _) =>
        {
            lock (_delays) _delays.Add(d);
            return Task.CompletedTask;
        };
        return p;
    }

    static ChatRequest Req(params AiMessage[] history) => new()
    {
        Model = "claude-opus-5-5",
        SystemPrompt = "You are an SSH ops assistant.",
        History = history,
        Tools = CatalogTools,
    };

    static Task<List<ChatEvent>> Collect(AnthropicProvider p, ChatRequest r) => CollectWith(p, r, TestContext.Current.CancellationToken);

    static async Task<List<ChatEvent>> CollectWith(AnthropicProvider p, ChatRequest r, CancellationToken ct)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in p.StreamAsync(r, ct)) list.Add(e);
        return list;
    }

    void Sse(params string[] bodies)
    {
        foreach (var b in bodies) _mock.PostResponses.Enqueue(MockResponse.Sse(b));
    }

    static void AssertNoNulls(JsonElement e, string path = "$")
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null:
                Assert.Fail("JSON null at " + path);
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) AssertNoNulls(p.Value, path + "." + p.Name);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var x in e.EnumerateArray()) AssertNoNulls(x, $"{path}[{i++}]");
                break;
        }
    }

    static List<string> Keys(JsonElement o) => o.EnumerateObject().Select(p => p.Name).ToList();

    static void JsonEqual(string expected, JsonElement actual) =>
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(expected).RootElement, actual), $"expected {expected}\nactual   {actual.GetRawText()}");

    // ================================================================== request: headers

    [Fact]
    public async Task Request_headers_default_config()
    {
        Sse(AnthropicFixtures.Final);
        await Collect(Provider(), Req(AiMessage.User("hi")));

        var r = Assert.Single(_mock.Posts);
        Assert.Equal("/v1/messages", r.PathAndQuery);
        Assert.Equal("application/json", r.Headers["content-type"]);
        Assert.Equal("2026-10-21", r.Headers["anthropic-version"]);
        Assert.Equal("test-key", r.Headers["x-api-key"]);
        Assert.False(r.Headers.ContainsKey("authorization"));
        Assert.Equal("server-side-fallback-2026-07-01,thinking-binding-controls-2026-08-01", r.Headers["anthropic-beta"]);
    }

    [Fact]
    public async Task Request_headers_bearer_updates_display_no_fallback_and_extra_headers()
    {
        Sse(AnthropicFixtures.Final);
        var p = Provider(c => c with
        {
            UseBearerAuth = true,
            ThinkingDisplay = AiThinkingDisplay.Updates,
            ServerSideFallback = false,
            ExtraHeaders = new Dictionary<string, string>
            {
                ["X-Gateway"] = "g1",
                ["Authorization"] = "Bearer from-extra",
                ["X-Api-Key"] = "from-extra",
                ["anthropic-beta"] = "custom-beta-1, thinking-binding-controls-2026-08-01",
            },
        });
        await Collect(p, Req(AiMessage.User("hi")));

        var r = Assert.Single(_mock.Posts);
        Assert.Equal("Bearer test-key", r.Headers["authorization"]);
        Assert.False(r.Headers.ContainsKey("x-api-key"));
        Assert.Equal("g1", r.Headers["x-gateway"]);
        Assert.Equal("thinking-display-updates-2026-08-18,thinking-binding-controls-2026-08-01,custom-beta-1", r.Headers["anthropic-beta"]);
        var b = r.Json;
        Assert.False(b.TryGetProperty("fallbacks", out _));
        Assert.Equal("updates", b.GetProperty("thinking").GetProperty("display").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task No_auth_header_without_key(string? key)
    {
        Sse(AnthropicFixtures.Final);
        await Collect(Provider(key: key), Req(AiMessage.User("hi")));
        var r = Assert.Single(_mock.Posts);
        Assert.False(r.Headers.ContainsKey("x-api-key"));
        Assert.False(r.Headers.ContainsKey("authorization"));
    }

    // ================================================================== request: body

    [Fact]
    public async Task Request_body_shape_matches_design_7_2()
    {
        Sse(AnthropicFixtures.Final);
        await Collect(Provider(), Req(AiMessage.User("hi")));
        var r = Assert.Single(_mock.Posts);
        var b = r.Json;
        AssertNoNulls(b);

        Assert.Equal(
            ["model", "max_tokens", "stream", "system", "tools", "tool_choice", "thinking", "output_config", "fallbacks", "cache_control", "messages"],
            Keys(b));
        Assert.Equal("claude-opus-5-5", b.GetProperty("model").GetString());
        Assert.Equal(32000, b.GetProperty("max_tokens").GetInt32());
        Assert.True(b.GetProperty("stream").GetBoolean());
        Assert.Equal("""[{"type":"text","text":"You are an SSH ops assistant.","cache_control":{"type":"ephemeral"}}]""", b.GetProperty("system").GetRawText());
        Assert.Equal("""{"type":"auto"}""", b.GetProperty("tool_choice").GetRawText());
        Assert.Equal("""{"type":"adaptive","display":"summarized","block_binding":{"prefix_mismatch_behavior":"drop_block"}}""", b.GetProperty("thinking").GetRawText());
        Assert.Equal("""{"effort":"medium"}""", b.GetProperty("output_config").GetRawText());
        Assert.Equal("default", b.GetProperty("fallbacks").GetString());
        Assert.Equal("""{"type":"ephemeral"}""", b.GetProperty("cache_control").GetRawText());
        Assert.Equal("""[{"role":"user","content":[{"type":"text","text":"hi"}]}]""", b.GetProperty("messages").GetRawText());

        var tools = b.GetProperty("tools").EnumerateArray().ToList();
        var names = tools.Select(t => t.GetProperty("name").GetString()!).ToList();
        Assert.Equal(CatalogTools.Select(t => t.Name).Order(StringComparer.Ordinal), names);
        for (var i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            var last = i == tools.Count - 1;
            Assert.Equal(last ? ["name", "description", "input_schema", "strict", "eager_input_streaming", "cache_control"]
                              : ["name", "description", "input_schema", "strict", "eager_input_streaming"], Keys(t));
            Assert.True(t.GetProperty("strict").GetBoolean());
            Assert.True(t.GetProperty("eager_input_streaming").GetBoolean());
            Assert.Equal(CatalogTools.Single(c => c.Name == names[i]).Description, t.GetProperty("description").GetString());
            var schema = t.GetProperty("input_schema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            foreach (var banned in new[] { "\"minimum\"", "\"maximum\"", "\"minLength\"", "\"maxLength\"", "\"default\"", "\"pattern\"" })
                Assert.DoesNotContain(banned, schema.GetRawText(), StringComparison.Ordinal);
        }
        Assert.Equal("""{"type":"ephemeral"}""", tools[^1].GetProperty("cache_control").GetRawText());

        // Constraints moved into the property description, derived from the catalog's own schema.
        var original = CatalogTools.Single(t => t.Name == ToolNames.RunCommand).InputSchema.GetProperty("properties").GetProperty("timeout_s");
        var sent = tools.Single(t => t.GetProperty("name").GetString() == ToolNames.RunCommand)
            .GetProperty("input_schema").GetProperty("properties").GetProperty("timeout_s");
        Assert.Equal(
            $"{original.GetProperty("description").GetString()} ({original.GetProperty("minimum")}–{original.GetProperty("maximum")}; default {original.GetProperty("default")})",
            sent.GetProperty("description").GetString());

        foreach (var banned in new[] { "temperature", "top_p", "top_k" })
            Assert.False(b.TryGetProperty(banned, out _), banned);
    }

    [Fact]
    public async Task Request_body_without_tools_omits_tool_fields_and_uses_requested_effort()
    {
        Sse(AnthropicFixtures.Final, AnthropicFixtures.Final);
        await Collect(Provider(c => c with { ThinkingDisplay = AiThinkingDisplay.Omitted }),
            Req(AiMessage.User("hi")) with { Tools = [], Effort = AiEffort.High, MaxTokens = 64000 });
        var b = _mock.Posts[0].Json;
        Assert.False(b.TryGetProperty("tools", out _));
        Assert.False(b.TryGetProperty("tool_choice", out _));
        Assert.Equal("high", b.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("omitted", b.GetProperty("thinking").GetProperty("display").GetString());
        Assert.Equal(64000, b.GetProperty("max_tokens").GetInt32());

        // Custom gateway preset: no eager_input_streaming; empty system prompt is omitted (an empty text block is a 400).
        await Collect(Provider(c => c with { EagerInputStreaming = false }), Req(AiMessage.User("hi")) with { SystemPrompt = "", Effort = AiEffort.Low });
        var b2 = _mock.Posts[1].Json;
        Assert.False(b2.TryGetProperty("system", out _));
        Assert.Equal("low", b2.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.All(b2.GetProperty("tools").EnumerateArray(), t => Assert.False(t.TryGetProperty("eager_input_streaming", out _)));
    }

    // ================================================================== streaming normalization

    [Fact]
    public async Task Stream_normalizes_thinking_text_and_fragmented_parallel_tool_use()
    {
        Sse(AnthropicFixtures.ToolUse);
        var events = await Collect(Provider(), Req(AiMessage.User("why did nginx fail?")));

        var kinds = events.Select(e => e switch
        {
            ThinkingDelta => "think",
            TextDelta => "text",
            ToolCallStarted s => "start:" + s.Name,
            ToolCallInputDelta => "in",
            ToolCallCompleted c => "done:" + c.Call.Name,
            Usage => "usage",
            Stopped s => "stop:" + s.Reason,
            _ => "?" + e.GetType().Name,
        }).ToList();
        Assert.Equal(
            ["think", "think", "text", "text",
             "start:read_terminal", "in", "in", "in", "done:read_terminal",
             "start:run_command", "in", "in", "in", "done:run_command",
             "usage", "stop:ToolUse"], kinds);

        Assert.Equal(AnthropicFixtures.ThinkingText1 + AnthropicFixtures.ThinkingText2, string.Concat(events.OfType<ThinkingDelta>().Select(t => t.Text)));
        Assert.Equal("""{"session": "s1", "lines": 50}""", string.Concat(events.OfType<ToolCallInputDelta>().Where(d => d.Id == "toolu_01").Select(d => d.PartialJson)));
        var completed = events.OfType<ToolCallCompleted>().Select(c => c.Call).ToList();
        Assert.All(completed, c => Assert.Null(c.InputError));
        Assert.Equal(50, completed[0].Input!.Value.GetProperty("lines").GetInt32());
        Assert.Equal("""{"session": "s1", "lines": 50}""", completed[0].RawInput);
        Assert.Equal("ss -ltnp 'sport = :80'", completed[1].Input!.Value.GetProperty("command").GetString());

        Assert.Equal(new Usage(812, 156, 2048, 0), events.OfType<Usage>().Single());

        var stop = Assert.IsType<Stopped>(events[^1]);
        Assert.Null(stop.Detail);
        Assert.Equal(AiErrorKind.None, stop.ErrorKind);
        var m = stop.Message!;
        Assert.Equal(AiRole.Assistant, m.Role);
        Assert.Equal("anthropic", m.RawProvider);
        Assert.Equal("claude-opus-5-5", m.ServedByModel);
        Assert.Equal("Let me look at the terminal and check what holds port 80.", m.Text);
        Assert.Equal(AnthropicFixtures.ThinkingText1 + AnthropicFixtures.ThinkingText2, m.Thinking);
        Assert.Equal(["toolu_01", "toolu_02"], m.ToolCalls.Select(c => c.Id));
        Assert.Equal(
            "[" +
            """{"type":"thinking","thinking":"The user asks why nginx failed. Read the terminal and check port 80.","signature":"EqQBCkgIARABGAIiQL2sigFAKEsignature=="},""" +
            """{"type":"text","text":"Let me look at the terminal and check what holds port 80."},""" +
            """{"type":"tool_use","id":"toolu_01","name":"read_terminal","input":{"session":"s1","lines":50},"caller":{"type":"direct"}},""" +
            """{"type":"tool_use","id":"toolu_02","name":"run_command","input":{"host":"prod-web-01","command":"ss -ltnp 'sport = :80'","timeout_s":30},"caller":{"type":"direct"}}""" +
            "]", m.RawContent);
    }

    [Fact]
    public async Task Raw_content_is_replayed_byte_identical_after_a_sqlite_round_trip_and_history_stays_append_only()
    {
        Sse(AnthropicFixtures.ToolUse, AnthropicFixtures.Final, AnthropicFixtures.Final);
        var p = Provider();
        var user = AiMessage.User("why did nginx fail? 日本語 <ok> & \"quotes\"");
        var first = (Stopped)(await Collect(p, Req(user)))[^1];

        // Persist like the conversation store does (TEXT column) and read it back.
        string stored;
        using (var db = new SqliteConnection("Data Source=:memory:"))
        {
            db.Open();
            using (var create = db.CreateCommand())
            {
                create.CommandText = "CREATE TABLE m (raw TEXT NOT NULL)";
                create.ExecuteNonQuery();
            }
            using (var insert = db.CreateCommand())
            {
                insert.CommandText = "INSERT INTO m VALUES ($raw)";
                insert.Parameters.AddWithValue("$raw", first.Message!.RawContent);
                insert.ExecuteNonQuery();
            }
            using var select = db.CreateCommand();
            select.CommandText = "SELECT raw FROM m";
            stored = (string)select.ExecuteScalar()!;
        }
        Assert.Equal(first.Message!.RawContent, stored);
        var assistant = first.Message with { RawContent = stored };

        var results = AiMessage.Results([new ToolResult("toolu_01", "nginx: [emerg] bind() to 0.0.0.0:80 failed", false), new ToolResult("toolu_02", "Denied by the user.", true)]);
        var second = (Stopped)(await Collect(p, Req(user, assistant, results)))[^1];
        var third = (await Collect(p, Req(user, assistant, results, second.Message!, AiMessage.User("thanks"))))[^1];
        Assert.IsType<Stopped>(third);

        var r1 = _mock.Posts[0].Json;
        var r2 = _mock.Posts[1].Json;
        var r3 = _mock.Posts[2].Json;
        AssertNoNulls(r2);
        AssertNoNulls(r3);

        var msgs2 = r2.GetProperty("messages");
        Assert.Equal(3, msgs2.GetArrayLength());
        Assert.Equal("assistant", msgs2[1].GetProperty("role").GetString());
        Assert.Equal(stored, msgs2[1].GetProperty("content").GetRawText()); // byte-identical
        Assert.Equal(
            """{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":"nginx: [emerg] bind() to 0.0.0.0:80 failed"},{"type":"tool_result","tool_use_id":"toolu_02","content":"Denied by the user.","is_error":true}]}""",
            msgs2[2].GetRawText());

        // Byte-stable prefix across all three requests: tools, system, and every earlier message.
        foreach (var r in new[] { r2, r3 })
        {
            Assert.Equal(r1.GetProperty("tools").GetRawText(), r.GetProperty("tools").GetRawText());
            Assert.Equal(r1.GetProperty("system").GetRawText(), r.GetProperty("system").GetRawText());
            Assert.Equal(r1.GetProperty("messages")[0].GetRawText(), r.GetProperty("messages")[0].GetRawText());
        }
        var msgs3 = r3.GetProperty("messages");
        Assert.Equal(5, msgs3.GetArrayLength());
        for (var i = 0; i < 3; i++)
            Assert.Equal(msgs2[i].GetRawText(), msgs3[i].GetRawText());
        Assert.Equal(second.Message!.RawContent, msgs3[3].GetProperty("content").GetRawText());
    }

    [Fact]
    public async Task Assistant_turn_from_another_provider_is_sent_as_text_and_tool_use_without_thinking()
    {
        Sse(AnthropicFixtures.Final);
        var input = JsonDocument.Parse("""{"session":"s1","lines":20}""").RootElement.Clone();
        var foreign = new AiMessage
        {
            Role = AiRole.Assistant,
            Text = "Let me check.",
            Thinking = "deepseek reasoning",
            ToolCalls = [new ToolCall("call_1", "read_terminal", input, """{"session":"s1","lines":20}"""), new ToolCall("call_2", "list_hosts", null, "{bad", "Tool input is not valid JSON")],
            RawProvider = "openai",
            RawContent = """{"role":"assistant","content":"Let me check."}""",
        };
        var results = AiMessage.Results([new ToolResult("call_1", "ok", false), new ToolResult("call_2", "{\"INVALID_JSON\":\"{bad\"}", true)]);
        await Collect(Provider(), Req(AiMessage.User("q"), foreign, results));

        var a = _mock.Posts[0].Json.GetProperty("messages")[1];
        Assert.Equal(
            """{"role":"assistant","content":[{"type":"text","text":"Let me check."},{"type":"tool_use","id":"call_1","name":"read_terminal","input":{"session":"s1","lines":20}},{"type":"tool_use","id":"call_2","name":"list_hosts","input":{}}]}""",
            a.GetRawText());
    }

    // ================================================================== stop reasons

    [Fact]
    public async Task Refusal_discards_partial_output()
    {
        Sse(AnthropicFixtures.Refusal);
        var events = await Collect(Provider(), Req(AiMessage.User("...")));
        Assert.Equal("Here is how to", Assert.IsType<TextDelta>(events.Single(e => e is TextDelta)).Text); // streamed for display
        var stop = Assert.IsType<Stopped>(events[^1]);
        Assert.Equal(StopReason.Refusal, stop.Reason);
        Assert.Null(stop.Message);
        Assert.Equal("cyber: This request was declined by a safety classifier.", stop.Detail);
        Assert.Equal(AiErrorKind.None, stop.ErrorKind);
    }

    [Fact]
    public async Task Max_tokens_keeps_the_turn()
    {
        Sse(AnthropicFixtures.MaxTokens);
        var stop = (Stopped)(await Collect(Provider(), Req(AiMessage.User("long"))))[^1];
        Assert.Equal(StopReason.MaxTokens, stop.Reason);
        Assert.Equal("The first half of a long", stop.Message!.Text);
        Assert.Null(stop.Message.Thinking); // display omitted-style empty block
        Assert.Equal("""[{"type":"thinking","thinking":"","signature":"SIG_T=="},{"type":"text","text":"The first half of a long"}]""", stop.Message.RawContent);
    }

    [Fact]
    public async Task Progress_update_blocks_stream_as_separate_thinking_notes()
    {
        Sse(AnthropicFixtures.ProgressUpdates);
        var events = await Collect(Provider(c => c with { ThinkingDisplay = AiThinkingDisplay.Updates }), Req(AiMessage.User("check")));
        Assert.Equal(["Checking the logs.", "\n\n", "Then the config."], events.OfType<ThinkingDelta>().Select(t => t.Text));
        var stop = (Stopped)events[^1];
        Assert.Equal(StopReason.ToolUse, stop.Reason);
        Assert.Equal("Checking the logs.\n\nThen the config.", stop.Message!.Thinking);
        Assert.Null(stop.Message.Text);
        var call = Assert.Single(stop.Message.ToolCalls);
        Assert.Equal(("toolu_q", "list_sessions", "{}"), (call.Id, call.Name, call.RawInput));
        Assert.Null(call.InputError);
        Assert.Equal(JsonValueKind.Object, call.Input!.Value.ValueKind);
        Assert.StartsWith("""[{"type":"thinking","thinking":"","signature":"SIG_R=="},{"type":"thinking","thinking":"Checking the logs.","signature":"SIG_U1=="}""", stop.Message.RawContent, StringComparison.Ordinal);
        Assert.EndsWith("""{"type":"tool_use","id":"toolu_q","name":"list_sessions","input":{},"caller":{"type":"direct"}}]""", stop.Message.RawContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_tool_json_sets_InputError_and_keeps_start_input_in_raw_content()
    {
        Sse(AnthropicFixtures.BadToolInput);
        var stop = (Stopped)(await Collect(Provider(), Req(AiMessage.User("go"))))[^1];
        Assert.Equal(StopReason.ToolUse, stop.Reason);
        var (bad, schemaBad) = (stop.Message!.ToolCalls[0], stop.Message.ToolCalls[1]);

        Assert.Equal("toolu_bad1", bad.Id);
        Assert.Null(bad.Input);
        Assert.Equal("""{"host":"h","command":"echo "hi"","timeout_s":5}""", bad.RawInput);
        Assert.StartsWith("Tool input is not valid JSON", bad.InputError, StringComparison.Ordinal);

        // Valid JSON that violates the schema is not this layer's business: the tool layer validates it.
        Assert.Null(schemaBad.InputError);
        Assert.Equal("fifty", schemaBad.Input!.Value.GetProperty("lines").GetString());

        // As in the spike: the unparseable call keeps the input of content_block_start ({}).
        Assert.Equal(
            """[{"type":"tool_use","id":"toolu_bad1","name":"run_command","input":{},"caller":{"type":"direct"}},{"type":"tool_use","id":"toolu_bad2","name":"read_terminal","input":{"session":"s1","lines":"fifty","extra":1},"caller":{"type":"direct"}}]""",
            stop.Message.RawContent);
    }

    [Fact]
    public async Task Unknown_block_and_delta_types_are_preserved_and_nulls_dropped()
    {
        Sse(AnthropicFixtures.UnknownTypes);
        var stop = (Stopped)(await Collect(Provider(), Req(AiMessage.User("x"))))[^1];
        Assert.Equal(StopReason.EndTurn, stop.Reason);
        Assert.Equal("Cited.", stop.Message!.Text);
        Assert.Equal(
            """[{"type":"mystery_block","payload":{"a":1},"note":"xyz","extra":{"b":2}},{"type":"text","text":"Cited.","citations":[{"type":"char_location","cited_text":"c","document_index":0,"start_char_index":0,"end_char_index":1}]}]""",
            stop.Message.RawContent);
    }

    [Fact]
    public async Task Complete_json_message_instead_of_sse_is_accepted()
    {
        _mock.PostResponses.Enqueue(MockResponse.Json(AnthropicFixtures.WholeMessageJson));
        var events = await Collect(Provider(), Req(AiMessage.User("x")));
        Assert.Equal("Short.", Assert.Single(events.OfType<ThinkingDelta>()).Text);
        Assert.Equal("Hello from JSON.", Assert.Single(events.OfType<TextDelta>()).Text);
        Assert.Equal("toolu_j", Assert.Single(events.OfType<ToolCallStarted>()).Id);
        Assert.Equal(new Usage(5, 6, 1, 2), Assert.Single(events.OfType<Usage>()));
        var stop = Assert.IsType<Stopped>(events[^1]);
        Assert.Equal(StopReason.ToolUse, stop.Reason);
        Assert.Equal("web", stop.Message!.ToolCalls.Single().Input!.Value.GetProperty("query").GetString());
        Assert.Equal(
            """[{"type":"thinking","thinking":"Short.","signature":"SIG_J=="},{"type":"text","text":"Hello from JSON."},{"type":"tool_use","id":"toolu_j","name":"list_hosts","input":{"query":"web"}}]""",
            stop.Message.RawContent);
    }

    // ================================================================== fallbacks

    [Fact]
    public async Task Pre_output_fallback_is_reported_and_replayed_unchanged()
    {
        Sse(AnthropicFixtures.FallbackPreOutput, AnthropicFixtures.Final);
        var p = Provider();
        var events = await Collect(p, Req(AiMessage.User("q1")));
        Assert.Equal(new FallbackOccurred("claude-opus-5-5", "claude-opus-4-8"), Assert.Single(events.OfType<FallbackOccurred>()));
        var stop = Assert.IsType<Stopped>(events[^1]);
        Assert.Equal(StopReason.EndTurn, stop.Reason);
        Assert.Equal("claude-opus-4-8", stop.Message!.ServedByModel);
        Assert.Equal("Answered by the fallback model.", stop.Message.Text);

        await Collect(p, Req(AiMessage.User("q1"), stop.Message, AiMessage.User("q2")));
        var replayed = _mock.Posts[1].Json.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(stop.Message.RawContent, replayed.GetRawText());
        Assert.Equal("""{"type":"fallback","from":{"model":"claude-opus-5-5"},"to":{"model":"claude-opus-4-8"},"trigger":{"type":"refusal","category":"cyber"}}""", replayed[0].GetRawText());
    }

    [Fact]
    public async Task Mid_stream_fallback_executes_only_calls_after_the_boundary_and_echoes_per_the_rules()
    {
        Sse(AnthropicFixtures.FallbackMidStream, AnthropicFixtures.Final, AnthropicFixtures.Final);
        var p = Provider();
        var events = await Collect(p, Req(AiMessage.User("scan")));

        // Both calls are displayed, the fallback switch is reported.
        Assert.Equal(["toolu_pre", "toolu_post"], events.OfType<ToolCallCompleted>().Select(c => c.Call.Id));
        Assert.Equal(new FallbackOccurred("claude-opus-5-5", "claude-opus-4-8"), Assert.Single(events.OfType<FallbackOccurred>()));

        var stop = Assert.IsType<Stopped>(events[^1]);
        Assert.Equal(StopReason.ToolUse, stop.Reason);
        var m = stop.Message!;
        Assert.Equal("toolu_post", Assert.Single(m.ToolCalls).Id); // the declined model's call is never executed
        Assert.Equal("claude-opus-4-8", m.ServedByModel);
        Assert.Equal("Partial answer. Checking the terminal.", m.Text);
        Assert.Equal("Plan the scan.\n\nRead the terminal instead.", m.Thinking);

        // RawContent keeps everything the API returned...
        using var raw = JsonDocument.Parse(m.RawContent!);
        Assert.Equal(["thinking", "text", "tool_use", "fallback", "thinking", "text", "tool_use"],
            raw.RootElement.EnumerateArray().Select(b => b.GetProperty("type").GetString()));

        // ...and the echo omits thinking / tool_use before the last fallback block, keeping text and the block in place.
        var results = AiMessage.Results([new ToolResult("toolu_post", "screen", false)]);
        await Collect(p, Req(AiMessage.User("scan"), m, results));
        var echoed = _mock.Posts[1].Json.GetProperty("messages")[1].GetProperty("content");
        var blocks = raw.RootElement.EnumerateArray().ToList();
        Assert.Equal("[" + string.Join(",", new[] { 1, 3, 4, 5, 6 }.Select(i => blocks[i].GetRawText())) + "]", echoed.GetRawText());

        // Deterministic: the next request repeats the same bytes.
        await Collect(p, Req(AiMessage.User("scan"), m, results, AiMessage.User("more")));
        Assert.Equal(echoed.GetRawText(), _mock.Posts[2].Json.GetProperty("messages")[1].GetProperty("content").GetRawText());
    }

    // ================================================================== errors

    [Theory]
    [InlineData(401, "authentication_error", AiErrorKind.Authentication)]
    [InlineData(403, "permission_error", AiErrorKind.Authentication)]
    [InlineData(400, "invalid_request_error", AiErrorKind.InvalidRequest)]
    [InlineData(404, "not_found_error", AiErrorKind.InvalidRequest)]
    [InlineData(413, "request_too_large", AiErrorKind.InvalidRequest)]
    [InlineData(422, "invalid_request_error", AiErrorKind.InvalidRequest)]
    [InlineData(402, "billing_error", AiErrorKind.Other)]
    [InlineData(502, "api_error", AiErrorKind.Other)]
    public async Task Http_errors_map_to_kinds_without_retry(int status, string type, AiErrorKind kind)
    {
        _mock.PostResponses.Enqueue(new MockResponse(status, "application/json",
            $$"""{"type":"error","error":{"type":"{{type}}","message":"the message"},"request_id":"req_body"}""",
            Headers: new Dictionary<string, string> { ["request-id"] = "req_hdr_1" }));
        var events = await Collect(Provider(), Req(AiMessage.User("x")));
        var stop = Assert.IsType<Stopped>(Assert.Single(events));
        Assert.Equal(StopReason.Error, stop.Reason);
        Assert.Equal(kind, stop.ErrorKind);
        Assert.Null(stop.Message);
        Assert.Equal($"HTTP {status} {type}: the message (request-id: req_hdr_1)", stop.Detail);
        Assert.Single(_mock.Posts);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Non_json_error_body_is_shown_and_request_id_from_body_is_used()
    {
        _mock.PostResponses.Enqueue(new MockResponse(404, "text/html", "<html>Not Found</html>"));
        _mock.PostResponses.Enqueue(MockResponse.Json("""{"type":"error","error":{"type":"invalid_request_error","message":"bad"},"request_id":"req_b"}""", 400));
        var a = (Stopped)Assert.Single(await Collect(Provider(), Req(AiMessage.User("x"))));
        Assert.Equal("HTTP 404 NotFound: <html>Not Found</html>", a.Detail);
        var b = (Stopped)Assert.Single(await Collect(Provider(), Req(AiMessage.User("x"))));
        Assert.Equal("HTTP 400 invalid_request_error: bad (request-id: req_b)", b.Detail);
    }

    [Theory]
    [InlineData(529, AiErrorKind.Overloaded)]
    [InlineData(503, AiErrorKind.Overloaded)]
    [InlineData(429, AiErrorKind.RateLimited)]
    [InlineData(500, AiErrorKind.Other)]
    public async Task Retryable_statuses_are_retried_twice_with_backoff(int status, AiErrorKind kind)
    {
        for (var i = 0; i < 3; i++)
            _mock.PostResponses.Enqueue(MockResponse.Json($$$"""{"type":"error","error":{"type":"x","message":"try {{{i}}}"}}""", status));
        var stop = Assert.IsType<Stopped>(Assert.Single(await Collect(Provider(), Req(AiMessage.User("x")))));
        Assert.Equal(kind, stop.ErrorKind);
        Assert.Contains("try 2", stop.Detail, StringComparison.Ordinal);
        Assert.Equal(3, _mock.Posts.Count);
        Assert.Equal(2, _delays.Count);
        Assert.InRange(_delays[0], TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(625));
        Assert.InRange(_delays[1], TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1250));
    }

    [Fact]
    public async Task Retry_succeeds_and_respects_retry_after()
    {
        _mock.PostResponses.Enqueue(new MockResponse(429, "application/json", """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""",
            Headers: new Dictionary<string, string> { ["retry-after"] = "3" }));
        _mock.PostResponses.Enqueue(MockResponse.Json("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", 529));
        Sse(AnthropicFixtures.Final);
        var events = await Collect(Provider(), Req(AiMessage.User("x")));
        Assert.Equal(StopReason.EndTurn, Assert.IsType<Stopped>(events[^1]).Reason);
        Assert.Equal(3, _mock.Posts.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), _delays[0]);
        Assert.Equal(2, _delays.Count);
        Assert.Equal(_mock.Posts[0].Body, _mock.Posts[2].Body); // identical retry
    }

    [Fact]
    public async Task No_retry_when_retry_after_is_too_long_or_server_says_no()
    {
        _mock.PostResponses.Enqueue(new MockResponse(429, "application/json", """{"type":"error","error":{"type":"rate_limit_error","message":"later"}}""",
            Headers: new Dictionary<string, string> { ["retry-after"] = "3600" }));
        _mock.PostResponses.Enqueue(new MockResponse(529, "application/json", """{"type":"error","error":{"type":"overloaded_error","message":"no"}}""",
            Headers: new Dictionary<string, string> { ["x-should-retry"] = "false" }));
        Assert.Equal(AiErrorKind.RateLimited, ((Stopped)Assert.Single(await Collect(Provider(), Req(AiMessage.User("x"))))).ErrorKind);
        Assert.Equal(AiErrorKind.Overloaded, ((Stopped)Assert.Single(await Collect(Provider(), Req(AiMessage.User("x"))))).ErrorKind);
        Assert.Equal(2, _mock.Posts.Count);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Stream_error_event_before_output_is_retried_then_reported()
    {
        Sse(AnthropicFixtures.OverloadedErrorEvent, AnthropicFixtures.Final);
        var ok = await Collect(Provider(), Req(AiMessage.User("x")));
        Assert.Equal(StopReason.EndTurn, Assert.IsType<Stopped>(ok[^1]).Reason);
        Assert.Equal(2, _mock.Posts.Count);

        Sse(AnthropicFixtures.OverloadedErrorEvent, AnthropicFixtures.OverloadedErrorEvent, AnthropicFixtures.OverloadedErrorEvent);
        var stop = Assert.IsType<Stopped>(Assert.Single(await Collect(Provider(), Req(AiMessage.User("x")))));
        Assert.Equal((StopReason.Error, AiErrorKind.Overloaded), (stop.Reason, stop.ErrorKind));
        Assert.Equal("Stream error overloaded_error: Overloaded (request-id: req_stream_1)", stop.Detail);
        Assert.Equal(5, _mock.Posts.Count);
    }

    [Fact]
    public async Task Stream_error_event_after_output_is_not_retried()
    {
        var body = AnthropicFixtures.Truncated + "\n" + AnthropicFixtures.OverloadedErrorEvent;
        Sse(body);
        var events = await Collect(Provider(), Req(AiMessage.User("x")));
        Assert.Equal("Hel", Assert.IsType<TextDelta>(events[0]).Text);
        var stop = Assert.IsType<Stopped>(events[^1]);
        Assert.Equal(AiErrorKind.Overloaded, stop.ErrorKind);
        Assert.Null(stop.Message);
        Assert.Single(_mock.Posts);
    }

    [Fact]
    public async Task Truncated_and_malformed_streams_end_with_MalformedResponse()
    {
        Sse(AnthropicFixtures.Truncated, AnthropicFixtures.Malformed, "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{}}\n\n");
        var truncated = await Collect(Provider(), Req(AiMessage.User("x")));
        Assert.Equal("Hel", Assert.IsType<TextDelta>(truncated[0]).Text);
        var s1 = Assert.IsType<Stopped>(truncated[^1]);
        Assert.Equal((StopReason.Error, AiErrorKind.MalformedResponse), (s1.Reason, s1.ErrorKind));
        Assert.Null(s1.Message);

        var s2 = Assert.IsType<Stopped>(Assert.Single(await Collect(Provider(), Req(AiMessage.User("x")))));
        Assert.Equal(AiErrorKind.MalformedResponse, s2.ErrorKind);
        Assert.Contains("content_block_start", s2.Detail, StringComparison.Ordinal);

        var s3 = Assert.IsType<Stopped>(Assert.Single(await Collect(Provider(), Req(AiMessage.User("x")))));
        Assert.Equal(AiErrorKind.MalformedResponse, s3.ErrorKind);
    }

    [Fact]
    public async Task Connection_failure_and_bad_base_url()
    {
        var refused = (Stopped)Assert.Single(await Collect(Provider(baseUrl: "http://127.0.0.1:1"), Req(AiMessage.User("x"))));
        Assert.Equal((StopReason.Error, AiErrorKind.Network), (refused.Reason, refused.ErrorKind));
        Assert.Empty(_delays);

        var bad = (Stopped)Assert.Single(await Collect(Provider(baseUrl: "not a url"), Req(AiMessage.User("x"))));
        Assert.Equal(AiErrorKind.InvalidRequest, bad.ErrorKind);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Provider(baseUrl: "ftp://x").ListModelsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AiErrorKind.InvalidRequest, ex.Kind);

        var exNet = await Assert.ThrowsAsync<AiProviderException>(() => Provider(baseUrl: "http://127.0.0.1:1").ListModelsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AiErrorKind.Network, exNet.Kind);
    }

    // ================================================================== cancellation

    [Fact]
    public async Task Cancellation_throws_OperationCanceledException_instead_of_Stopped()
    {
        _mock.PostResponses.Enqueue(new MockResponse(200, "text/event-stream", AnthropicFixtures.ToolUse, ChunkDelayMs: 200));
        using var cts = new CancellationTokenSource();
        var seen = new List<ChatEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in Provider().StreamAsync(Req(AiMessage.User("x")), cts.Token))
            {
                seen.Add(e);
                cts.Cancel();
            }
        });
        Assert.Single(seen);
        Assert.IsNotType<Stopped>(seen[0]);

        using var pre = new CancellationTokenSource();
        await pre.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectWith(Provider(), Req(AiMessage.User("x")), pre.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider().ListModelsAsync(pre.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider().ProbeAsync("claude-opus-5-5", pre.Token));
    }

    [Fact]
    public async Task Cancellation_during_retry_backoff_throws()
    {
        _mock.PostResponses.Enqueue(MockResponse.Json("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", 529));
        using var cts = new CancellationTokenSource();
        var p = Provider();
        p.Delay = (_, ct) =>
        {
            cts.Cancel();
            return Task.Delay(Timeout.Infinite, ct);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectWith(p, Req(AiMessage.User("x")), cts.Token));
        Assert.Single(_mock.Posts);
    }

    // ================================================================== models

    [Fact]
    public async Task List_models_follows_paging()
    {
        _mock.GetResponses.Enqueue(MockResponse.Json(AnthropicFixtures.ModelsPage1));
        _mock.GetResponses.Enqueue(MockResponse.Json(AnthropicFixtures.ModelsPage2));
        var models = await Provider().ListModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            [new ModelInfo("claude-opus-5-5", "Claude Opus 5.5"), new ModelInfo("claude-sonnet-5-5", "Claude Sonnet 5.5"), new ModelInfo("claude-haiku-5-5")],
            models);
        var gets = _mock.Requests.Where(r => r.Method == "GET").ToList();
        Assert.Equal(["/v1/models?limit=100", "/v1/models?limit=100&after_id=claude-opus-5-5"], gets.Select(g => g.PathAndQuery));
        Assert.All(gets, g =>
        {
            Assert.Equal("test-key", g.Headers["x-api-key"]);
            Assert.Equal("2026-10-21", g.Headers["anthropic-version"]);
        });
    }

    [Theory]
    [InlineData(401, AiErrorKind.Authentication)]
    [InlineData(404, AiErrorKind.InvalidRequest)]
    public async Task List_models_errors_throw_AiProviderException(int status, AiErrorKind kind)
    {
        _mock.GetResponses.Enqueue(MockResponse.Json("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", status));
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Provider().ListModelsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(status, ex.Status);
        Assert.Contains("invalid x-api-key", ex.Message, StringComparison.Ordinal);

        _mock.GetResponses.Enqueue(MockResponse.Json("""{"nope":1}"""));
        var malformed = await Assert.ThrowsAsync<AiProviderException>(() => Provider().ListModelsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AiErrorKind.MalformedResponse, malformed.Kind);
    }

    // ================================================================== probe

    [Fact]
    public async Task Probe_lists_models_and_sends_one_tiny_request_with_one_tool()
    {
        _mock.ModelsJson = AnthropicFixtures.ModelsSinglePage;
        Sse(AnthropicFixtures.Final);
        var result = await Provider().ProbeAsync("claude-opus-5-5", TestContext.Current.CancellationToken);
        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal(["claude-opus-5-5", "claude-sonnet-5-5"], result.Models.Select(m => m.Id));
        Assert.True(result.Detected.Tools);
        Assert.True(result.Detected.Streaming);

        var b = Assert.Single(_mock.Posts).Json;
        Assert.Equal("claude-opus-5-5", b.GetProperty("model").GetString());
        Assert.Equal("low", b.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.InRange(b.GetProperty("max_tokens").GetInt32(), 256, 4096);
        Assert.Equal("connection_test", Assert.Single(b.GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Probe_detects_an_endpoint_without_tools()
    {
        _mock.ModelsJson = AnthropicFixtures.ModelsSinglePage;
        _mock.PostResponses.Enqueue(MockResponse.Json("""{"type":"error","error":{"type":"invalid_request_error","message":"tools: Extra inputs are not permitted"}}""", 400));
        Sse(AnthropicFixtures.Final);
        var result = await Provider().ProbeAsync("local-model", TestContext.Current.CancellationToken);
        Assert.True(result.Ok);
        Assert.False(result.Detected.Tools);
        Assert.False(_mock.Posts[1].Json.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Probe_reports_failures_without_throwing()
    {
        _mock.GetResponses.Enqueue(MockResponse.Json("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", 401));
        var auth = await Provider().ProbeAsync("claude-opus-5-5", TestContext.Current.CancellationToken);
        Assert.False(auth.Ok);
        Assert.Empty(auth.Models);
        Assert.Equal("HTTP 401 authentication_error: invalid x-api-key", auth.Error);
        Assert.Empty(_mock.Posts);

        // A gateway without /v1/models: the request decides. Unknown model → 404 twice is not retried without tools.
        _mock.GetResponses.Enqueue(MockResponse.Json("""{"type":"error","error":{"type":"not_found_error","message":"no models endpoint"}}""", 404));
        _mock.PostResponses.Enqueue(MockResponse.Json("""{"type":"error","error":{"type":"not_found_error","message":"model: nope"}}""", 404));
        _mock.PostResponses.Enqueue(MockResponse.Json("""{"type":"error","error":{"type":"not_found_error","message":"model: nope"}}""", 404));
        var missing = await Provider().ProbeAsync("nope", TestContext.Current.CancellationToken);
        Assert.False(missing.Ok);
        Assert.Equal("HTTP 404 not_found_error: model: nope", missing.Error);

        var down = await Provider(baseUrl: "http://127.0.0.1:1").ProbeAsync("claude-opus-5-5", TestContext.Current.CancellationToken);
        Assert.False(down.Ok);
        Assert.NotNull(down.Error);
    }
}
