using System.Text.Json;

namespace MySSH.Ai.Tests;

public sealed class OpenAIStreamingTests : OpenAITestBase
{
    [Fact]
    public async Task Interleaved_tool_call_fragments_are_assembled_by_index()
    {
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.ToolCalls));
        var events = await Collect(OpenAI(), Req("gpt-test", null, AiMessage.User("check")));

        Assert.Equal("Checking.", string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
        Assert.Equal([("call_A", "read_terminal"), ("call_B", "run_command")], events.OfType<ToolCallStarted>().Select(s => (s.Id, s.Name)));
        var deltas = events.OfType<ToolCallInputDelta>().ToList();
        Assert.Equal(["call_A", "call_B", "call_A", "call_B"], deltas.Select(d => d.Id));
        var completed = events.OfType<ToolCallCompleted>().Select(c => c.Call).ToList();
        Assert.Equal(["call_A", "call_B"], completed.Select(c => c.Id));
        Assert.All(completed, c => Assert.Null(c.InputError));
        Assert.Equal("""{"session":"s1","lines":50}""", completed[0].RawInput);
        Assert.Equal("uptime", completed[1].Input!.Value.GetProperty("command").GetString());

        var usage = events.OfType<Usage>().Single();
        Assert.Equal((70L, 40L, 30L, 0L), (usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens));

        var stop = Stop(events);
        Assert.Equal(StopReason.ToolUse, stop.Reason);
        var msg = stop.Message!;
        Assert.Equal((AiRole.Assistant, "Checking.", "openai", "gpt-test-2026-01-01"), (msg.Role, msg.Text, msg.RawProvider, msg.ServedByModel));
        Assert.Equal(["call_A", "call_B"], msg.ToolCalls.Select(c => c.Id));
        Assert.Null(msg.Thinking);

        var r = Mock.Posts.Single();
        Assert.Equal("/v1/chat/completions", r.PathAndQuery);
        Assert.Equal("Bearer sk-test", r.Headers["authorization"]);
        var b = r.Json;
        Assert.Equal("gpt-test", b.GetProperty("model").GetString());
        Assert.True(b.GetProperty("stream").GetBoolean());
        Assert.True(b.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("auto", b.GetProperty("tool_choice").GetString());
        Assert.True(b.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal(1000, b.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(b.TryGetProperty("max_tokens", out _));
        Assert.False(b.TryGetProperty("temperature", out _));
        var tools = b.GetProperty("tools");
        Assert.Equal(["read_terminal", "run_command"], tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()));
        Assert.All(tools.EnumerateArray(), t => Assert.False(t.GetProperty("function").TryGetProperty("strict", out _)));
        Assert.Equal("command", tools[1].GetProperty("function").GetProperty("parameters").GetProperty("required")[0].GetString());
        Assert.Equal(["system", "user"], Roles(b.GetProperty("messages")));
        Assert.Equal("sys", b.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Replay_round_trips_through_a_string_and_sends_one_tool_message_per_result()
    {
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.ToolCalls));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        var p = OpenAI();
        var first = Stop(await Collect(p, Req("gpt-test", null, AiMessage.User("check"))));
        var stored = ThroughStorage(first.Message!);

        var second = await Collect(p, Req("gpt-test", null, AiMessage.User("check"), stored,
            AiMessage.Results([new ToolResult("call_A", "out-A", false), new ToolResult("call_B", "denied by user", true)])));
        var stop = Stop(second);
        Assert.Equal(StopReason.EndTurn, stop.Reason);
        Assert.Equal("Port 80 is in use.", stop.Message!.Text);
        Assert.Empty(stop.Message.ToolCalls);

        var msgs = Mock.Posts[1].Json.GetProperty("messages");
        Assert.Equal(["system", "user", "assistant", "tool", "tool"], Roles(msgs));
        var a = msgs[2];
        Assert.Equal(JsonValueKind.String, a.GetProperty("content").ValueKind); // plain string: DeepSeek & co. reject part arrays here
        Assert.Equal("Checking.", ContentText(a));
        var tcs = a.GetProperty("tool_calls");
        Assert.Equal(["call_A", "call_B"], tcs.EnumerateArray().Select(t => t.GetProperty("id").GetString()));
        Assert.Equal("function", tcs[0].GetProperty("type").GetString());
        Assert.Equal("read_terminal", tcs[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"session":"s1","lines":50}""", tcs[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("""{"host":"prod-web-01","command":"uptime","timeout_s":10}""", tcs[1].GetProperty("function").GetProperty("arguments").GetString());
        Assert.False(a.TryGetProperty("reasoning_content", out _));
        Assert.Equal(("call_A", "out-A"), (msgs[3].GetProperty("tool_call_id").GetString(), msgs[3].GetProperty("content").GetString()));
        Assert.Equal(("call_B", "ERROR: denied by user"), (msgs[4].GetProperty("tool_call_id").GetString(), msgs[4].GetProperty("content").GetString()));
    }

    [Fact]
    public async Task Raw_content_is_the_assistant_message_json()
    {
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.ToolCalls));
        var stop = Stop(await Collect(OpenAI(), Req("gpt-test", null, AiMessage.User("check"))));
        var raw = JsonDocument.Parse(stop.Message!.RawContent!).RootElement;
        Assert.Equal("assistant", raw.GetProperty("role").GetString());
        Assert.Equal("Checking.", raw.GetProperty("content").GetString());
        Assert.Equal("""{"session":"s1","lines":50}""", raw.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
    }

    [Fact]
    public async Task Stop_with_tool_calls_is_tool_use_and_repeated_index_with_new_id_is_a_new_call()
    {
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.ToolCallWithStop));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.ToolCallsSameIndex));
        var p = OpenAI();

        var a = Stop(await Collect(p, Req("m", null, AiMessage.User("x"))));
        Assert.Equal(StopReason.ToolUse, a.Reason);
        Assert.Equal("call_X", a.Message!.ToolCalls.Single().Id);
        Assert.Null(a.Message.Text);

        var events = await Collect(p, Req("m", null, AiMessage.User("x")));
        var calls = Stop(events).Message!.ToolCalls;
        Assert.Equal([("call_1", "a", """{"x":1}"""), ("call_2", "b", """{"y":2}""")], calls.Select(c => (c.Id, c.Name, c.RawInput)));
        Assert.Equal(2, events.OfType<ToolCallStarted>().Count());
    }

    [Fact]
    public async Task Invalid_tool_arguments_set_input_error_and_still_replay_verbatim()
    {
        var bad = OpenAIFixtures.ToolCalls.Replace("\\\"lines\\\":50}", "\\\"lines\\\":50,}", StringComparison.Ordinal);
        Mock.PostResponses.Enqueue(MockResponse.Sse(bad));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        var p = OpenAI();
        var stop = Stop(await Collect(p, Req("gpt-test", null, AiMessage.User("check"))));
        var call = stop.Message!.ToolCalls[0];
        Assert.Null(call.Input);
        Assert.NotNull(call.InputError);
        Assert.Equal("""{"session":"s1","lines":50,}""", call.RawInput);
        Assert.Null(stop.Message.ToolCalls[1].InputError);

        await Collect(p, Req("gpt-test", null, AiMessage.User("check"), ThroughStorage(stop.Message),
            AiMessage.Results([new ToolResult("call_A", """{"INVALID_JSON":"..."}""", true), new ToolResult("call_B", "ok", false)])));
        var msgs = Mock.Posts[1].Json.GetProperty("messages");
        Assert.Equal("""{"session":"s1","lines":50,}""", msgs[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.StartsWith("ERROR: ", msgs[3].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finish_reasons_map_to_stop_reasons()
    {
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Length));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.ContentFilter));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Truncated));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.StreamError));
        var p = OpenAI();

        var length = Stop(await Collect(p, Req("m", null, AiMessage.User("x"))));
        Assert.Equal((StopReason.MaxTokens, "Partial"), (length.Reason, length.Message!.Text));

        var filtered = Stop(await Collect(p, Req("m", null, AiMessage.User("x"))));
        Assert.Equal(StopReason.Refusal, filtered.Reason);
        Assert.Null(filtered.Message);

        var truncated = Stop(await Collect(p, Req("m", null, AiMessage.User("x"))));
        Assert.Equal((StopReason.Error, AiErrorKind.MalformedResponse), (truncated.Reason, truncated.ErrorKind));
        Assert.Null(truncated.Message);

        var streamError = Stop(await Collect(p, Req("m", null, AiMessage.User("x"))));
        Assert.Equal(StopReason.Error, streamError.Reason);
        Assert.Contains("upstream model crashed", streamError.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eos", StopReason.EndTurn)]
    [InlineData("model_length", StopReason.MaxTokens)]
    public async Task Non_standard_finish_reasons_end_the_turn_instead_of_failing_it(string finishReason, StopReason expected)
    {
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.UnknownFinish(finishReason)));
        var stop = Stop(await Collect(Custom(), Req("m", null, AiMessage.User("x"))));
        Assert.Equal((expected, "Done here."), (stop.Reason, stop.Message!.Text));
        Assert.Contains(finishReason, stop.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepSeek_reasoning_is_surfaced_and_echoed_only_within_the_tool_loop()
    {
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.DeepSeekReasoning));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.DeepSeekReasoningToolCall));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        var p = DeepSeek();
        Assert.Equal("deepseek", p.ProviderId);

        var events = await Collect(p, Req("deepseek-reasoner", [], AiMessage.User("q")));
        Assert.Equal("Think about port 80.", string.Concat(events.OfType<ThinkingDelta>().Select(t => t.Text)));
        Assert.Equal("Port 80 is busy.", string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
        var usage = events.OfType<Usage>().Single();
        Assert.Equal((6L, 20L, 4L), (usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens));
        var turn1 = Stop(events);
        Assert.Equal(StopReason.EndTurn, turn1.Reason);
        Assert.Equal(("Think about port 80.", "deepseek-reasoner", "deepseek"), (turn1.Message!.Thinking, turn1.Message.ServedByModel, turn1.Message.RawProvider));

        var r0 = Mock.Posts[0];
        Assert.Equal("/chat/completions", r0.PathAndQuery); // DeepSeek base has no /v1
        Assert.Equal("Bearer ds-test", r0.Headers["authorization"]);
        Assert.Equal(1000, r0.Json.GetProperty("max_tokens").GetInt32());
        Assert.False(r0.Json.TryGetProperty("max_completion_tokens", out _));
        Assert.False(r0.Json.TryGetProperty("tools", out _)); // no tools in the request

        // Second user turn with a tool call: reasoning of the earlier user turn is not sent back.
        var h1 = ThroughStorage(turn1.Message);
        var turn2 = Stop(await Collect(p, Req("deepseek-reasoner", null, AiMessage.User("q"), h1, AiMessage.User("q2"))));
        Assert.Equal(StopReason.ToolUse, turn2.Reason);
        Assert.Equal("Need the terminal.", turn2.Message!.Thinking);
        Assert.False(Mock.Posts[1].Json.GetProperty("messages")[2].TryGetProperty("reasoning_content", out _));

        // Same tool loop: reasoning_content travels back on the assistant tool-call message.
        var h2 = ThroughStorage(turn2.Message);
        var results = AiMessage.Results([new ToolResult("call_D", "out", false)]);
        await Collect(p, Req("deepseek-reasoner", null, AiMessage.User("q"), h1, AiMessage.User("q2"), h2, results));
        var msgs = Mock.Posts[2].Json.GetProperty("messages");
        Assert.Equal(["system", "user", "assistant", "user", "assistant", "tool"], Roles(msgs));
        Assert.False(msgs[2].TryGetProperty("reasoning_content", out _));
        Assert.Equal("Need the terminal.", msgs[4].GetProperty("reasoning_content").GetString());
        Assert.Equal("call_D", msgs[4].GetProperty("tool_calls")[0].GetProperty("id").GetString());

        // Next user turn: the finished loop's reasoning is dropped.
        var h3 = ThroughStorage(Stop(await Collect(p, Req("deepseek-reasoner", null, AiMessage.User("q"), h1, AiMessage.User("q2"), h2, results))).Message!);
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        await Collect(p, Req("deepseek-reasoner", null, AiMessage.User("q"), h1, AiMessage.User("q2"), h2, results, h3, AiMessage.User("q3")));
        Assert.False(Mock.Posts[4].Json.GetProperty("messages")[4].TryGetProperty("reasoning_content", out _));
    }

    [Fact]
    public async Task No_tools_capability_omits_tools_and_no_streaming_capability_uses_one_completion()
    {
        Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.NonStreaming));
        var p = OpenAI(new ProviderCapabilities(Tools: false, Streaming: false));
        var events = await Collect(p, Req("local-model", null, AiMessage.User("hello")));

        Assert.Equal("Hello from a non-streaming server.", events.OfType<TextDelta>().Single().Text);
        var usage = events.OfType<Usage>().Single();
        Assert.Equal((5L, 6L), (usage.InputTokens, usage.OutputTokens));
        var stop = Stop(events);
        Assert.Equal((StopReason.EndTurn, "Hello from a non-streaming server.", "local-model"), (stop.Reason, stop.Message!.Text, stop.Message.ServedByModel));

        var b = Mock.Posts.Single().Json;
        Assert.False(b.TryGetProperty("tools", out _));
        Assert.False(b.TryGetProperty("tool_choice", out _));
        Assert.False(b.TryGetProperty("parallel_tool_calls", out _));
        Assert.False(b.TryGetProperty("stream", out var s) && s.GetBoolean());
    }

    [Fact]
    public async Task Non_streaming_tool_call_with_reasoning_produces_the_same_events()
    {
        Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.NonStreamingToolCall));
        var events = await Collect(DeepSeek(new ProviderCapabilities(Tools: true, Streaming: false)), Req("deepseek-reasoner", null, AiMessage.User("x")));

        Assert.Equal("Check it.", events.OfType<ThinkingDelta>().Single().Text);
        Assert.Equal("Let me look.", events.OfType<TextDelta>().Single().Text);
        Assert.Equal(("call_N", "read_terminal"), events.OfType<ToolCallStarted>().Select(s => (s.Id, s.Name)).Single());
        Assert.Equal("""{"lines":3}""", events.OfType<ToolCallInputDelta>().Single().PartialJson);
        Assert.Equal(3, events.OfType<ToolCallCompleted>().Single().Call.Input!.Value.GetProperty("lines").GetInt32());
        var stop = Stop(events);
        Assert.Equal(StopReason.ToolUse, stop.Reason);
        Assert.Equal("Check it.", JsonDocument.Parse(stop.Message!.RawContent!).RootElement.GetProperty("reasoning_content").GetString());
        Assert.Equal(2, Mock.Posts.Single().Json.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task Other_providers_history_uses_normalized_fields_and_unanswered_calls_get_error_results()
    {
        var anthropicTurn = new AiMessage
        {
            Role = AiRole.Assistant, Text = "Looking.", RawProvider = "anthropic", RawContent = """[{"type":"thinking"}]""",
            ToolCalls = [new ToolCall("toolu_1", "read_terminal", Schema("""{"session":"s1","lines":5}"""), """{"session":"s1","lines":5}""")],
        };
        // An OpenAI turn cut off by max_tokens: its call was never executed, then the user spoke again.
        var cutOff = new AiMessage
        {
            Role = AiRole.Assistant, RawProvider = "openai-compatible",
            RawContent = """{"role":"assistant","content":null,"tool_calls":[{"id":"call_cut","type":"function","function":{"name":"run_command","arguments":"{\"comm"}}]}""",
        };
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        await Collect(OpenAI(), Req("gpt-test", null, AiMessage.User("q"), anthropicTurn,
            AiMessage.Results([new ToolResult("toolu_1", "out", false)]), cutOff, AiMessage.User("go on")));

        var msgs = Mock.Posts[0].Json.GetProperty("messages");
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "user"], Roles(msgs));
        Assert.Equal("Looking.", ContentText(msgs[2]));
        Assert.Equal("toolu_1", msgs[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("""{"session":"s1","lines":5}""", msgs[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("toolu_1", msgs[3].GetProperty("tool_call_id").GetString());
        // openai-compatible raw is foreign to the "openai" provider: normalized fields (none) are used.
        Assert.False(msgs[4].TryGetProperty("tool_calls", out var tc) && tc.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Own_raw_tool_calls_without_results_are_answered_with_an_error_tool_message()
    {
        var cutOff = new AiMessage
        {
            Role = AiRole.Assistant, RawProvider = "openai",
            RawContent = """{"role":"assistant","content":"Partial","tool_calls":[{"id":"call_cut","type":"function","function":{"name":"run_command","arguments":"{\"comm"}}]}""",
        };
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        await Collect(OpenAI(), Req("gpt-test", null, AiMessage.User("q"), cutOff, AiMessage.User("go on")));

        var msgs = Mock.Posts[0].Json.GetProperty("messages");
        Assert.Equal(["system", "user", "assistant", "tool", "user"], Roles(msgs));
        Assert.Equal("""{"comm""", msgs[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("call_cut", msgs[3].GetProperty("tool_call_id").GetString());
        Assert.StartsWith("ERROR: ", msgs[3].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keyless_custom_endpoint_sends_no_authorization_and_extra_headers_never_override_auth()
    {
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        var custom = Custom(headers: new Dictionary<string, string> { ["X-Gateway"] = "g1", ["Authorization"] = "Bearer evil", ["x-api-key"] = "evil" });
        Assert.Equal("openai-compatible", custom.ProviderId);
        var stop = Stop(await Collect(custom, Req("llama3", null, AiMessage.User("hi"))));
        Assert.Equal("openai-compatible", stop.Message!.RawProvider);

        var r = Mock.Posts[0];
        Assert.Equal("/v1/chat/completions", r.PathAndQuery);
        Assert.False(r.Headers.ContainsKey("authorization"));
        Assert.False(r.Headers.ContainsKey("x-api-key"));
        Assert.Equal("g1", r.Headers["x-gateway"]);
        Assert.Equal(1000, r.Json.GetProperty("max_tokens").GetInt32());

        await Collect(OpenAI(headers: new Dictionary<string, string> { ["Authorization"] = "Bearer evil", ["X-Trace"] = "t" }), Req("gpt-test", null, AiMessage.User("hi")));
        Assert.Equal("Bearer sk-test", Mock.Posts[1].Headers["authorization"]);
        Assert.Equal("t", Mock.Posts[1].Headers["x-trace"]);
    }

    [Fact]
    public async Task Extra_headers_are_sent_on_model_listing_too_and_trailing_slash_base_works()
    {
        Mock.ModelsJson = OpenAIFixtures.Models;
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        var p = OpenAI(headers: new Dictionary<string, string> { ["X-Gateway"] = "g1" }, baseSuffix: "/v1/");
        await p.ListModelsAsync(Ct);
        await Collect(p, Req("gpt-test", null, AiMessage.User("hi")));

        var get = Mock.Requests.First(r => r.Method == "GET");
        Assert.Equal(("/v1/models", "g1"), (get.PathAndQuery, get.Headers["x-gateway"]));
        Assert.Equal("/v1/chat/completions", Mock.Posts[0].PathAndQuery);
    }

    static string? ContentText(JsonElement message)
    {
        var c = message.GetProperty("content");
        return c.ValueKind switch
        {
            JsonValueKind.String => c.GetString(),
            JsonValueKind.Array => string.Concat(c.EnumerateArray().Select(p => p.GetProperty("text").GetString())),
            _ => null,
        };
    }
}
