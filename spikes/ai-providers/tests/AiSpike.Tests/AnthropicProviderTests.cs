using System.Text.Json;
using AiSpike.Core;

namespace AiSpike.Tests;

[Collection(EnvCollection.Name)] // never run concurrently with the env-var tests
public sealed class AnthropicProviderTests : IAsyncLifetime
{
    private MockServer _mock = null!;
    private AnthropicProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _mock = await MockServer.StartAsync();
        _provider = new AnthropicProvider(new AnthropicProviderOptions { BaseUrl = _mock.BaseUrl, ApiKey = "test-key", MaxRetries = 0 });
    }

    public async Task DisposeAsync() => await _mock.DisposeAsync();

    private static readonly IAgentTool[] Tools = [new ReadTerminalTool(), new RunCommandTool()];

    private static ChatRequest Req(params AiMessage[] history) => new()
    {
        Model = AnthropicProvider.DefaultModel,
        SystemPrompt = "You are an SSH ops assistant.",
        History = history,
        Tools = Tools.Select(t => t.Definition).ToList(),
    };

    private async Task<List<ChatEvent>> Collect(ChatRequest req)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in _provider.StreamAsync(req, CancellationToken.None)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Stream_normalizes_thinking_text_and_fragmented_tool_use()
    {
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicToolUse));
        var events = await Collect(Req(AiMessage.User("why did nginx fail?")));

        var kinds = events.Select(e => e switch
        {
            ThinkingSummaryDelta => "think",
            TextDelta => "text",
            ToolCallStarted s => "start:" + s.Name,
            ToolCallInputDelta => "in",
            ToolCallCompleted c => "done:" + c.Call.Name,
            Usage => "usage",
            Stopped s => "stop:" + s.Reason,
            _ => "?",
        }).ToList();
        Assert.Equal(
            ["think", "think", "text", "text",
             "start:read_terminal", "in", "in", "in", "in", "done:read_terminal",
             "start:run_command", "in", "in", "in", "done:run_command",
             "usage", "stop:ToolUse"], kinds);

        Assert.Equal(ThinkingText1 + ThinkingText2, string.Concat(events.OfType<ThinkingSummaryDelta>().Select(t => t.Text)));
        var calls = events.OfType<ToolCallCompleted>().Select(c => c.Call).ToList();
        Assert.All(calls, c => Assert.Null(c.InputError));
        Assert.Equal(50, calls[0].Input!.Value.GetProperty("lines").GetInt32());
        Assert.Equal("ss -ltnp 'sport = :80'", calls[1].Input!.Value.GetProperty("command").GetString());

        var usage = events.OfType<Usage>().Single();
        Assert.Equal((812L, 156L, 2048L), (usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens));

        var stop = (Stopped)events[^1];
        Assert.Equal(2, stop.Message!.ToolCalls.Count);
        Assert.Equal("claude-opus-5-5", stop.Message.ServedByModel);
    }

    private const string ThinkingText1 = Fixtures.ThinkingText1;
    private const string ThinkingText2 = Fixtures.ThinkingText2;

    [Fact]
    public async Task Request_shape_matches_design_7_2()
    {
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicFinal));
        await Collect(Req(AiMessage.User("hi")));

        var r = _mock.Posts.Single();
        Assert.Equal("/v1/messages", r.PathAndQuery);            // beta endpoint = same path + anthropic-beta header
        Assert.Equal("Bearer test-key", r.Headers["authorization"]); // SDK 13.x sends the API key as Bearer
        Assert.False(r.Headers.ContainsKey("x-api-key"));
        Assert.Contains("server-side-fallback-2026-07-01", r.Headers["anthropic-beta"]);

        var b = r.Json;
        Assert.Equal("claude-opus-5-5", b.GetProperty("model").GetString());
        Assert.True(b.GetProperty("stream").GetBoolean());
        Assert.Equal("default", b.GetProperty("fallbacks").GetString());
        Assert.Equal("adaptive", b.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("summarized", b.GetProperty("thinking").GetProperty("display").GetString());
        Assert.Equal("medium", b.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("auto", b.GetProperty("tool_choice").GetProperty("type").GetString());
        foreach (var banned in new[] { "temperature", "top_p", "top_k" })
            Assert.False(b.TryGetProperty(banned, out _), banned);

        var system = b.GetProperty("system");
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());

        var tools = b.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(["read_terminal", "run_command"], tools.Select(t => t.GetProperty("name").GetString()));
        Assert.All(tools, t =>
        {
            Assert.True(t.GetProperty("strict").GetBoolean());
            Assert.True(t.GetProperty("eager_input_streaming").GetBoolean());
            Assert.False(t.GetProperty("input_schema").GetProperty("additionalProperties").GetBoolean());
            Assert.Equal("object", t.GetProperty("input_schema").GetProperty("type").GetString());
            Assert.DoesNotContain("\"minimum\"", t.GetProperty("input_schema").GetRawText());
        });
        Assert.Contains("lines.maximum=2000", tools[0].GetProperty("description").GetString());
        Assert.DoesNotContain(": null", r.Body.Replace(" ", "").Replace(":null", ": null")); // no explicit nulls
        Assert.False(tools[0].TryGetProperty("cache_control", out _));
        Assert.Equal("ephemeral", tools[1].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Refusal_maps_to_Stopped_Refusal_with_category()
    {
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicRefusal));
        var events = await Collect(Req(AiMessage.User("...")));
        var stop = Assert.IsType<Stopped>(events[^1]);
        Assert.Equal(StopReason.Refusal, stop.Reason);
        Assert.Contains("category=cyber", stop.Detail);

        // Agent loop: refused partial is not appended and nothing runs.
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicRefusal));
        var history = new List<AiMessage> { AiMessage.User("...") };
        var res = await new AgentLoop(_provider, Tools, new FixedApproval(true)).RunAsync(AnthropicProvider.DefaultModel, "sys", history, null, default);
        Assert.Equal(StopReason.Refusal, res.LastStop);
        Assert.Single(history);
    }

    [Fact]
    public async Task Server_side_fallback_block_is_parsed_and_replayed()
    {
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicFallbackServed));
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicFinal));
        var events = await Collect(Req(AiMessage.User("q1")));
        var stop = Assert.IsType<Stopped>(events[^1]);
        Assert.Equal(StopReason.EndTurn, stop.Reason);
        Assert.Equal("claude-opus-4-8", stop.Message!.ServedByModel);
        Assert.Equal("Answered by the fallback model.", stop.Message.Text);

        await Collect(Req(AiMessage.User("q1"), stop.Message, AiMessage.User("q2")));
        var replayed = _mock.Posts[1].Json.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("fallback", replayed[0].GetProperty("type").GetString());
        Assert.Equal("claude-opus-5-5", replayed[0].GetProperty("from").GetProperty("model").GetString());
        Assert.Equal("cyber", replayed[0].GetProperty("trigger").GetProperty("category").GetString());
        Assert.Equal("text", replayed[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Two_step_agent_loop_with_parallel_tools_and_unchanged_history_replay()
    {
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicToolUse));
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicFinal));
        var runCommand = new RunCommandTool();
        var approval = new FixedApproval(false); // console default: auto-deny
        var loop = new AgentLoop(_provider, [new ReadTerminalTool(), runCommand], approval);
        var history = new List<AiMessage> { AiMessage.User("why did nginx fail?") };

        var res = await loop.RunAsync(AnthropicProvider.DefaultModel, "sys", history, null, default);

        Assert.Equal((2, StopReason.EndTurn), (res.Steps, res.LastStop));
        Assert.Equal(4, history.Count); // user, assistant(tool_use), tool_results, assistant(final)
        Assert.Equal("nginx failed because port 80 is already in use.", history[3].Text);
        Assert.Single(approval.Asked);
        Assert.Empty(runCommand.Executed);

        var second = _mock.Posts[1].Json.GetProperty("messages");
        Assert.Equal(3, second.GetArrayLength());

        // Prior assistant content replayed unchanged: thinking (+signature), text, both tool_use blocks.
        var a = second[1];
        Assert.Equal("assistant", a.GetProperty("role").GetString());
        var c = a.GetProperty("content");
        Assert.Equal(4, c.GetArrayLength());
        Assert.Equal("thinking", c[0].GetProperty("type").GetString());
        Assert.Equal(ThinkingText1 + ThinkingText2, c[0].GetProperty("thinking").GetString());
        Assert.Equal(Fixtures.Signature1, c[0].GetProperty("signature").GetString());
        Assert.Equal("Let me look at the terminal and check what holds port 80.", c[1].GetProperty("text").GetString());
        Assert.Equal("toolu_01", c[2].GetProperty("id").GetString());
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse("""{"session":"s1","lines":50}""").RootElement, c[2].GetProperty("input")));
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse("""{"host":"prod-web-01","command":"ss -ltnp 'sport = :80'","timeout_s":30}""").RootElement,
            c[3].GetProperty("input")));

        // All tool results in ONE user message; denied run_command is is_error.
        var u = second[2];
        Assert.Equal("user", u.GetProperty("role").GetString());
        var results = u.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(["toolu_01", "toolu_02"], results.Select(x => x.GetProperty("tool_use_id").GetString()));
        Assert.False(results[0].TryGetProperty("is_error", out var e0) && e0.GetBoolean());
        Assert.True(results[1].GetProperty("is_error").GetBoolean());

        Assert.DoesNotContain(":null", _mock.Posts[1].Body.Replace(" ", "")); // no explicit nulls sent

        // Byte-stable prefix: system + tools identical between the two requests (cache + preserved thinking).
        var first = _mock.Posts[0].Json;
        Assert.Equal(first.GetProperty("system").GetRawText(), _mock.Posts[1].Json.GetProperty("system").GetRawText());
        Assert.Equal(first.GetProperty("tools").GetRawText(), _mock.Posts[1].Json.GetProperty("tools").GetRawText());
        Assert.Equal(first.GetProperty("messages")[0].GetRawText(), second[0].GetRawText());
    }

    [Fact]
    public async Task Invalid_or_schema_violating_tool_input_is_not_executed_and_returns_is_error()
    {
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicBadToolInput));
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicFinal));
        var runCommand = new RunCommandTool();
        var approval = new FixedApproval(true);
        var history = new List<AiMessage> { AiMessage.User("go") };
        var res = await new AgentLoop(_provider, [new ReadTerminalTool(), runCommand], approval)
            .RunAsync(AnthropicProvider.DefaultModel, "sys", history, null, default);

        Assert.Equal(StopReason.EndTurn, res.LastStop);
        Assert.Empty(approval.Asked);       // never reached approval
        Assert.Empty(runCommand.Executed);
        var results = history[2].ToolResults;
        Assert.All(results, r => Assert.True(r.IsError));
        Assert.Contains("INVALID_JSON", results[0].Content);
        Assert.Contains("expected integer", results[1].Content);
        Assert.Contains("additional property", results[1].Content);
    }

    [Fact]
    public async Task Max_steps_and_cancellation()
    {
        for (int i = 0; i < 3; i++) _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicToolUse));
        var history = new List<AiMessage> { AiMessage.User("loop") };
        var res = await new AgentLoop(_provider, Tools, new FixedApproval(false), new AgentOptions { MaxSteps = 2 })
            .RunAsync(AnthropicProvider.DefaultModel, "sys", history, null, default);
        Assert.True(res.HitMaxSteps);
        Assert.Equal(2, _mock.Posts.Count);

        // Cancel while waiting for approval: loop throws OperationCanceledException.
        using var cts = new CancellationTokenSource();
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicToolUse));
        var cancelling = new CancelOnAsk(cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AgentLoop(_provider, Tools, cancelling).RunAsync(AnthropicProvider.DefaultModel, "sys",
                [AiMessage.User("x")], null, cts.Token));
    }

    private sealed class CancelOnAsk(CancellationTokenSource cts) : IApproval
    {
        public Task<bool> ApproveAsync(ToolCall call, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task Lists_models_from_mock()
    {
        _mock.ModelsJson = Fixtures.AnthropicModels;
        var models = await _provider.ListModelsAsync(default);
        Assert.Equal(["claude-opus-5-5", "claude-sonnet-5-5"], models.Select(m => m.Id));
        Assert.Equal("Claude Opus 5.5", models[0].DisplayName);
        var get = _mock.Requests.Single(r => r.Method == "GET");
        Assert.StartsWith("/v1/models", get.PathAndQuery);
        Assert.Equal("Bearer test-key", get.Headers["authorization"]);
    }

    [Fact]
    public async Task Http_error_maps_to_Stopped_Error()
    {
        _mock.PostResponses.Enqueue(MockResponse.Json("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", 529));
        var events = await Collect(Req(AiMessage.User("x")));
        var stop = Assert.IsType<Stopped>(Assert.Single(events));
        Assert.Equal(StopReason.Error, stop.Reason);
    }
}
