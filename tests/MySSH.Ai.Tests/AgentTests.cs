using System.Runtime.CompilerServices;
using System.Text.Json;
using MySSH.Core;
using MySSH.Tools;

namespace MySSH.Ai.Tests;

/// <summary>A provider that plays back scripted turns and records the requests it got.</summary>
internal sealed class ScriptedProvider(params Func<ChatRequest, IEnumerable<ChatEvent>>[] turns) : IChatProvider
{
    readonly Queue<Func<ChatRequest, IEnumerable<ChatEvent>>> _turns = new(turns);
    public List<ChatRequest> Requests { get; } = [];
    public string ProviderId => "anthropic";
    public ProviderCapabilities Capabilities { get; init; } = new();
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    public Task<ProviderProbeResult> ProbeAsync(string model, CancellationToken ct) => Task.FromResult(new ProviderProbeResult(true, [], new()));

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        foreach (var e in _turns.Dequeue()(request))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return e;
        }
    }

    public static ToolCall Call(string id, string name, string json) => new(id, name, JsonDocument.Parse(json).RootElement.Clone(), json);

    public static IEnumerable<ChatEvent> ToolTurn(params ToolCall[] calls)
    {
        yield return new TextDelta("Checking.");
        foreach (var c in calls) yield return new ToolCallCompleted(c);
        yield return new Stopped(StopReason.ToolUse, null, new AiMessage
        {
            Role = AiRole.Assistant, Text = "Checking.", ToolCalls = calls, RawProvider = "anthropic", RawContent = "[\"raw\"]",
        });
    }

    public static IEnumerable<ChatEvent> Final(string text)
    {
        yield return new TextDelta(text);
        yield return new Usage(10, 5, 0, 0);
        yield return new Stopped(StopReason.EndTurn, null, new AiMessage { Role = AiRole.Assistant, Text = text, RawProvider = "anthropic", RawContent = "[]" });
    }
}

internal sealed class RecordingTools(Func<ToolCallRequest, ToolCallResult>? handler = null) : IToolInvoker
{
    public List<(ToolCallRequest Request, ToolActor Actor, AiToolCallInfo? Scope)> Calls { get; } = [];
    public TaskCompletionSource? Gate { get; set; }

    public async Task<ToolCallResult> InvokeAsync(ToolCallRequest request, ToolActor actor, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add((request, actor, AiToolCallScope.Current));
        if (Gate is { } g) await g.Task.WaitAsync(ct);
        return handler?.Invoke(request) ?? new ToolCallResult(false, $"ok:{request.ToolName}");
    }
}

public sealed class AgentTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly ToolActor Actor = AiAgent.Actor(AiProviderConfig.Claude(), "claude-opus-5-5");

    static AgentTurn Turn(IReadOnlyList<AiMessage>? history = null, Guid? conversation = null) =>
        new(conversation ?? Guid.NewGuid(), history ?? [], "why did nginx fail?", "claude-opus-5-5", AiEffort.Medium,
            [new AiToolSpec("read_terminal", "d", JsonDocument.Parse("{}").RootElement)], Actor);

    static async Task<List<AgentEvent>> Run(AiAgent agent, AgentTurn turn)
    {
        var list = new List<AgentEvent>();
        await foreach (var e in agent.RunAsync(turn, Ct)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Tool_calls_run_through_the_tool_layer_and_results_go_back_in_one_message()
    {
        var provider = new ScriptedProvider(
            _ => ScriptedProvider.ToolTurn(ScriptedProvider.Call("t1", "read_terminal", """{"session":"s-1","lines":50}"""),
                                           ScriptedProvider.Call("t2", "list_hosts", "{}")),
            _ => ScriptedProvider.Final("Port 80 is taken."));
        var tools = new RecordingTools();
        var conversation = Guid.NewGuid();
        var events = await Run(new AiAgent(provider, tools), Turn(conversation: conversation));

        Assert.Equal(2, tools.Calls.Count);
        Assert.All(tools.Calls, c => Assert.Equal("ai:anthropic/claude-opus-5-5", c.Actor.Label));
        Assert.All(tools.Calls, c => Assert.Equal(ConnectionOrigin.Ai, c.Actor.Origin));
        Assert.Contains(tools.Calls, c => c.Scope == new AiToolCallInfo(conversation, "t1", "read_terminal"));

        var appended = events.OfType<AgentHistoryAppended>().Select(e => e.Message).ToList();
        Assert.Equal([AiRole.User, AiRole.Assistant, AiRole.ToolResults, AiRole.Assistant], appended.Select(m => m.Role));
        Assert.Equal(["t1", "t2"], appended[2].ToolResults.Select(r => r.ToolCallId)); // call order kept
        Assert.Equal(AgentStop.Completed, events.OfType<AgentFinished>().Single().Stop);

        // The second request carries the whole append-only history, assistant raw content untouched.
        var second = provider.Requests[1].History;
        Assert.Equal(3, second.Count);
        Assert.Equal("[\"raw\"]", second[1].RawContent);
        Assert.Same(appended[1], second[1]);
    }

    [Fact]
    public async Task Invalid_tool_input_is_answered_with_an_error_and_never_executed()
    {
        var bad = new ToolCall("t1", "run_command", null, "{\"command\":\"rm -rf /", "Unterminated string");
        var provider = new ScriptedProvider(_ => ScriptedProvider.ToolTurn(bad), _ => ScriptedProvider.Final("Sorry."));
        var tools = new RecordingTools();
        var events = await Run(new AiAgent(provider, tools), Turn());
        Assert.Empty(tools.Calls);
        var result = events.OfType<AgentToolFinished>().Single().Result;
        Assert.True(result.IsError);
        Assert.Equal("{\"command\":\"rm -rf /", JsonDocument.Parse(result.Content).RootElement.GetProperty("INVALID_JSON").GetString());
    }

    [Fact]
    public async Task Max_tokens_with_tool_calls_answers_them_without_running_them()
    {
        var provider = new ScriptedProvider(_ => new ChatEvent[]
        {
            new Stopped(StopReason.MaxTokens, null, new AiMessage { Role = AiRole.Assistant, ToolCalls = [ScriptedProvider.Call("t1", "run_command", "{}")] }),
        });
        var tools = new RecordingTools();
        var events = await Run(new AiAgent(provider, tools), Turn());
        Assert.Empty(tools.Calls);
        var appended = events.OfType<AgentHistoryAppended>().Select(e => e.Message).ToList();
        Assert.Equal(AiRole.ToolResults, appended[^1].Role);
        Assert.True(appended[^1].ToolResults.Single().IsError);
        Assert.Equal(AgentStop.MaxTokens, events.OfType<AgentFinished>().Single().Stop);
    }

    [Fact]
    public async Task Refusal_and_errors_stop_the_turn_without_appending_partial_output()
    {
        var provider = new ScriptedProvider(_ => new ChatEvent[] { new TextDelta("partial"), new Stopped(StopReason.Refusal, "cyber", null) });
        var events = await Run(new AiAgent(provider, new RecordingTools()), Turn());
        Assert.Single(events.OfType<AgentHistoryAppended>()); // only the user message
        Assert.Equal(("cyber", AgentStop.Refusal), (events.OfType<AgentFinished>().Single().Detail, events.OfType<AgentFinished>().Single().Stop));

        provider = new ScriptedProvider(_ => new ChatEvent[] { new Stopped(StopReason.Error, "401 invalid x-api-key", null, AiErrorKind.Authentication) });
        var finished = (await Run(new AiAgent(provider, new RecordingTools()), Turn())).OfType<AgentFinished>().Single();
        Assert.Equal((AgentStop.Error, AiErrorKind.Authentication), (finished.Stop, finished.ErrorKind));
    }

    [Fact]
    public async Task Step_limit_ends_an_endless_tool_loop()
    {
        var turns = Enumerable.Range(0, 5).Select(i => (Func<ChatRequest, IEnumerable<ChatEvent>>)(_ =>
            ScriptedProvider.ToolTurn(ScriptedProvider.Call($"t{i}", "list_hosts", "{}")))).ToArray();
        var events = await Run(new AiAgent(new ScriptedProvider(turns), new RecordingTools(), new AgentOptions { MaxSteps = 3 }), Turn());
        var finished = events.OfType<AgentFinished>().Single();
        Assert.Equal((AgentStop.StepLimit, 3), (finished.Stop, finished.Steps));
    }

    [Fact]
    public async Task Tool_output_is_truncated_and_redacted_before_it_goes_back()
    {
        var provider = new ScriptedProvider(_ => ScriptedProvider.ToolTurn(ScriptedProvider.Call("t1", "sftp_read", "{}")), _ => ScriptedProvider.Final("done"));
        var tools = new RecordingTools(_ => new ToolCallResult(false, "DB_PASSWORD=hunter2hunter2\n" + new string('x', 500)));
        var events = await Run(new AiAgent(provider, tools, new AgentOptions { MaxToolOutputChars = 100 }), Turn());
        var content = events.OfType<AgentToolFinished>().Single().Result.Content;
        Assert.DoesNotContain("hunter2", content, StringComparison.Ordinal);
        Assert.Contains("[truncated:", content, StringComparison.Ordinal);
        Assert.Equal(1, events.OfType<AgentRedacted>().Single().Count);
        Assert.DoesNotContain("hunter2", provider.Requests[1].History[^1].ToolResults[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_during_a_tool_call_leaves_a_history_the_next_turn_repairs()
    {
        var provider = new ScriptedProvider(_ => ScriptedProvider.ToolTurn(ScriptedProvider.Call("t1", "run_command", "{}")), _ => ScriptedProvider.Final("ok"));
        var tools = new RecordingTools { Gate = new TaskCompletionSource() };
        using var cts = new CancellationTokenSource();
        var history = new List<AiMessage>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in new AiAgent(provider, tools).RunAsync(Turn(), cts.Token))
            {
                if (e is AgentHistoryAppended a) history.Add(a.Message);
                if (e is AgentToolStarted) await cts.CancelAsync();
            }
        });
        Assert.Equal(AiRole.Assistant, history[^1].Role); // tool_use without results

        var events = await Run(new AiAgent(provider, new RecordingTools()), Turn(history));
        var appended = events.OfType<AgentHistoryAppended>().Select(e => e.Message).ToList();
        Assert.Equal(AiRole.ToolResults, appended[0].Role);
        Assert.True(appended[0].ToolResults.Single().IsError);
        Assert.Equal(AiRole.User, appended[1].Role);
    }

    [Fact]
    public async Task Providers_without_tool_support_get_no_tools()
    {
        var provider = new ScriptedProvider(_ => ScriptedProvider.Final("hi")) { Capabilities = new ProviderCapabilities(Tools: false) };
        await Run(new AiAgent(provider, new RecordingTools()), Turn());
        Assert.Empty(provider.Requests.Single().Tools);
    }

    [Fact]
    public void History_entries_round_trip_through_json()
    {
        var m = new AiMessage
        {
            Role = AiRole.Assistant, Text = "t", RawProvider = "anthropic",
            RawContent = """[{"type":"thinking","thinking":"x","signature":"EqQB=="},{"type":"tool_use","id":"t1","name":"a","input":{"n":1}}]""",
            ToolCalls = [ScriptedProvider.Call("t1", "a", """{"n":1}""")], ServedByModel = "claude-opus-5-5", Thinking = "x",
        };
        var back = AiHistorySerializer.Deserialize(AiHistorySerializer.Serialize(m));
        Assert.Equal(m.RawContent, back.RawContent);
        Assert.Equal(1, back.ToolCalls[0].Input!.Value.GetProperty("n").GetInt32());
        Assert.Equal(m with { ToolCalls = back.ToolCalls, ToolResults = back.ToolResults }, back);
        Assert.Empty(back.ToolResults);
    }

    [Theory]
    [InlineData("```sh\nsudo ss -ltnp | grep :80\n```", "sudo ss -ltnp | grep :80")]
    [InlineData("```\n$ df -h\n```", "df -h")]
    [InlineData("uptime", "uptime")]
    [InlineData("```sh\n```", null)]
    public void Command_suggestion_takes_the_first_line_of_the_code_block(string reply, string? expected) =>
        Assert.Equal(expected, AiCommandSuggester.ExtractCommand(reply));
}
