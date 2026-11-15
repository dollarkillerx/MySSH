using System.Text.Json;
using AiSpike.Core;

namespace AiSpike.Tests;

public sealed class OpenAICompatibleProviderTests : IAsyncLifetime
{
    private MockServer _mock = null!;

    public async Task InitializeAsync() => _mock = await MockServer.StartAsync();
    public async Task DisposeAsync() => await _mock.DisposeAsync();

    private OpenAICompatibleProvider OpenAI(ProviderCapabilities? caps = null) => new(OpenAICompatibleOptions.OpenAI("sk-test") with
    {
        BaseUrl = _mock.BaseUrl + "/v1", MaxRetries = 0, Capabilities = caps ?? new(true, true),
    });

    private OpenAICompatibleProvider DeepSeek() => new(OpenAICompatibleOptions.DeepSeek("ds-test") with { BaseUrl = _mock.BaseUrl, MaxRetries = 0 });

    private static readonly IAgentTool[] Tools = [new ReadTerminalTool(), new RunCommandTool()];

    private static async Task<List<ChatEvent>> Collect(IChatProvider p, ChatRequest req)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in p.StreamAsync(req, default)) list.Add(e);
        return list;
    }

    private static ChatRequest Req(string model, IReadOnlyList<ToolDefinition>? tools = null, params AiMessage[] h) => new()
    {
        Model = model, SystemPrompt = "sys", History = h, Tools = tools ?? Tools.Select(t => t.Definition).ToList(),
    };

    [Fact]
    public async Task Fragmented_parallel_tool_calls_are_assembled_by_index()
    {
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.OpenAIToolCalls));
        var events = await Collect(OpenAI(), Req("gpt-test", null, AiMessage.User("check")));

        Assert.Equal("Checking.", string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
        Assert.Equal(["read_terminal", "run_command"], events.OfType<ToolCallStarted>().Select(s => s.Name));
        Assert.Equal(4, events.OfType<ToolCallInputDelta>().Count());
        var calls = events.OfType<ToolCallCompleted>().Select(c => c.Call).ToList();
        Assert.Equal(["call_A", "call_B"], calls.Select(c => c.Id));
        Assert.All(calls, c => Assert.Null(c.InputError));
        Assert.Equal("""{"session":"s1","lines":50}""", calls[0].RawInput);
        Assert.Equal("uptime", calls[1].Input!.Value.GetProperty("command").GetString());
        var usage = events.OfType<Usage>().Single();
        Assert.Equal((100L, 40L), (usage.InputTokens, usage.OutputTokens));
        var stop = Assert.IsType<Stopped>(events[^1]);
        Assert.Equal(StopReason.ToolUse, stop.Reason);

        var r = _mock.Posts.Single();
        Assert.Equal("/v1/chat/completions", r.PathAndQuery);
        Assert.Equal("Bearer sk-test", r.Headers["authorization"]);
        var b = r.Json;
        Assert.True(b.GetProperty("stream").GetBoolean());
        Assert.True(b.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("auto", b.GetProperty("tool_choice").GetString());
        Assert.Equal(2, b.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task Two_step_agent_loop_and_history_replay()
    {
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.OpenAIToolCalls));
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.OpenAIFinal));
        var run = new RunCommandTool();
        var approval = new FixedApproval(true); // allow this time
        var history = new List<AiMessage> { AiMessage.User("check") };
        var res = await new AgentLoop(OpenAI(), [new ReadTerminalTool(), run], approval).RunAsync("gpt-test", "sys", history, null, default);

        Assert.Equal((2, StopReason.EndTurn), (res.Steps, res.LastStop));
        Assert.Equal(["uptime"], run.Executed);
        Assert.Equal("Port 80 is in use.", history[^1].Text);

        var msgs = _mock.Posts[1].Json.GetProperty("messages");
        // system, user, assistant(tool_calls), tool, tool
        Assert.Equal(["system", "user", "assistant", "tool", "tool"], msgs.EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        var a = msgs[2];
        var tcs = a.GetProperty("tool_calls");
        Assert.Equal("call_A", tcs[0].GetProperty("id").GetString());
        Assert.Equal("""{"session":"s1","lines":50}""", tcs[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("""{"host":"prod-web-01","command":"uptime","timeout_s":10}""", tcs[1].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("call_A", msgs[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("call_B", msgs[4].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task DeepSeek_reasoning_content_is_surfaced_and_replayed()
    {
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.DeepSeekReasoning));
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.OpenAIFinal));
        var p = DeepSeek();
        var events = await Collect(p, Req("deepseek-reasoner", [], AiMessage.User("q")));
        Assert.Equal("Think about port 80.", string.Concat(events.OfType<ThinkingSummaryDelta>().Select(t => t.Text)));
        Assert.Equal("Port 80 is busy.", string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
        var stop = Assert.IsType<Stopped>(events[^1]);
        Assert.Equal(StopReason.EndTurn, stop.Reason);
        Assert.Equal("/chat/completions", _mock.Posts[0].PathAndQuery); // DeepSeek base has no /v1

        // Replay: reasoning_content is written back onto the assistant message via JsonPatch.
        await Collect(p, Req("deepseek-reasoner", [], AiMessage.User("q"), stop.Message!, AiMessage.User("q2")));
        var a = _mock.Posts[1].Json.GetProperty("messages")[2];
        Assert.Equal("assistant", a.GetProperty("role").GetString());
        Assert.Equal("Think about port 80.", a.GetProperty("reasoning_content").GetString());
    }

    [Fact]
    public async Task No_tools_capability_omits_tools_and_no_streaming_capability_uses_single_response()
    {
        _mock.PostResponses.Enqueue(MockResponse.Json(Fixtures.OpenAINonStreaming));
        var p = OpenAI(new ProviderCapabilities(Tools: false, Streaming: false));
        var history = new List<AiMessage> { AiMessage.User("hello") };
        var res = await new AgentLoop(p, Tools, new FixedApproval(false)).RunAsync("local-model", "sys", history, null, default);

        Assert.Equal(StopReason.EndTurn, res.LastStop);
        Assert.Equal("Hello from a non-streaming server.", history[^1].Text);
        var b = _mock.Posts.Single().Json;
        Assert.False(b.TryGetProperty("tools", out _));
        Assert.False(b.TryGetProperty("stream", out var s) && s.GetBoolean());
    }

    [Fact]
    public async Task Lists_models_for_both_presets()
    {
        _mock.ModelsJson = Fixtures.OpenAIModels;
        var a = await OpenAI().ListModelsAsync(default);
        var d = await DeepSeek().ListModelsAsync(default);
        Assert.Equal(["gpt-test", "deepseek-chat"], a.Select(m => m.Id));
        Assert.Equal(a.Select(m => m.Id), d.Select(m => m.Id));
        var gets = _mock.Requests.Where(r => r.Method == "GET").Select(r => r.PathAndQuery).ToList();
        Assert.Equal(["/v1/models", "/models"], gets);
    }

    [Fact]
    public async Task Malformed_tool_arguments_become_is_error_result()
    {
        var bad = Fixtures.OpenAIToolCalls.Replace("\\\"lines\\\":50}", "\\\"lines\\\":50");
        _mock.PostResponses.Enqueue(MockResponse.Sse(bad));
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.OpenAIFinal));
        var history = new List<AiMessage> { AiMessage.User("check") };
        await new AgentLoop(OpenAI(), Tools, new FixedApproval(false)).RunAsync("gpt-test", "sys", history, null, default);
        var results = history[2].ToolResults;
        Assert.True(results[0].IsError);
        Assert.Contains("INVALID_JSON", results[0].Content);
        var toolMsg = _mock.Posts[1].Json.GetProperty("messages")[3];
        Assert.StartsWith("ERROR: ", toolMsg.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Cross_provider_history_degrades_to_text_and_tool_calls()
    {
        // History produced by Anthropic replayed to OpenAI: raw content ignored, normalized fields used.
        var anthropicTurn = new AiMessage
        {
            Role = AiRole.Assistant, Text = "Looking.", RawProvider = "anthropic", RawContent = "[]",
            ToolCalls = [new ToolCall("toolu_1", "read_terminal", JsonDocument.Parse("""{"session":"s1","lines":5}""").RootElement, """{"session":"s1","lines":5}""", null)],
        };
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.OpenAIFinal));
        await Collect(OpenAI(), Req("gpt-test", null, AiMessage.User("q"), anthropicTurn,
            AiMessage.Results([new ToolResult("toolu_1", "out", false)])));
        var msgs = _mock.Posts[0].Json.GetProperty("messages");
        Assert.Equal("toolu_1", msgs[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("toolu_1", msgs[3].GetProperty("tool_call_id").GetString());
    }
}
