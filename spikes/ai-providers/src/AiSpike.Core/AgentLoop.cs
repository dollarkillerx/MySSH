using System.Text.Json;

namespace AiSpike.Core;

public interface IAgentTool
{
    ToolDefinition Definition { get; }
    bool RequiresApproval { get; }
    Task<string> ExecuteAsync(JsonElement input, CancellationToken ct);
}

public interface IApproval
{
    Task<bool> ApproveAsync(ToolCall call, CancellationToken ct);
}

public sealed class FixedApproval(bool allow) : IApproval
{
    public List<ToolCall> Asked { get; } = [];
    public Task<bool> ApproveAsync(ToolCall call, CancellationToken ct)
    {
        lock (Asked) Asked.Add(call);
        return Task.FromResult(allow);
    }
}

public sealed record AgentOptions
{
    public int MaxSteps { get; init; } = 25;
    public int MaxToolOutputChars { get; init; } = 64 * 1024;
}

public sealed record AgentResult(int Steps, StopReason LastStop, string? Detail, bool HitMaxSteps);

/// <summary>Design §7.3 loop: stream → stop checks → parallel tools → one tool_results message.</summary>
public sealed class AgentLoop(IChatProvider provider, IReadOnlyList<IAgentTool> tools, IApproval approval, AgentOptions? options = null)
{
    private readonly AgentOptions _opts = options ?? new();

    public async Task<AgentResult> RunAsync(string model, string systemPrompt, List<AiMessage> history,
        Action<ChatEvent>? onEvent, CancellationToken ct)
    {
        var defs = provider.Capabilities.Tools ? tools.Select(t => t.Definition).ToList() : [];
        for (int step = 1; step <= _opts.MaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            Stopped? stopped = null;
            await foreach (var ev in provider.StreamAsync(new ChatRequest
            {
                Model = model, SystemPrompt = systemPrompt, History = history, Tools = defs,
            }, ct))
            {
                onEvent?.Invoke(ev);
                if (ev is Stopped s) stopped = s;
            }
            if (stopped is null) return new AgentResult(step, StopReason.Error, "no Stopped event", false);

            switch (stopped.Reason)
            {
                case StopReason.Refusal:
                    // Partial output of a refused turn is discarded (not appended), tools never run.
                    return new AgentResult(step, StopReason.Refusal, stopped.Detail, false);
                case StopReason.Error:
                    return new AgentResult(step, StopReason.Error, stopped.Detail, false);
                case StopReason.MaxTokens:
                    // Tool input may be truncated: append the turn as-is (append-only), but run nothing.
                    history.Add(stopped.Message!);
                    return new AgentResult(step, StopReason.MaxTokens, stopped.Detail, false);
            }

            var assistant = stopped.Message!;
            history.Add(assistant); // unchanged, incl. raw thinking blocks
            if (stopped.Reason != StopReason.ToolUse || assistant.ToolCalls.Count == 0)
                return new AgentResult(step, stopped.Reason, stopped.Detail, false);

            // Run every call of this turn in parallel; all results go into ONE message, in call order.
            var results = await Task.WhenAll(assistant.ToolCalls.Select(c => ExecuteOneAsync(c, ct)));
            history.Add(AiMessage.Results(results));
        }
        return new AgentResult(_opts.MaxSteps, StopReason.ToolUse, "max steps reached", true);
    }

    private async Task<ToolResult> ExecuteOneAsync(ToolCall call, CancellationToken ct)
    {
        if (call.InputError is not null)
            return new ToolResult(call.Id, call.InputError.StartsWith('{') ? call.InputError : "Invalid tool input: " + call.InputError, true);
        var tool = tools.FirstOrDefault(t => t.Definition.Name == call.Name);
        if (tool is null) return new ToolResult(call.Id, $"Unknown tool '{call.Name}'", true);

        if (tool.RequiresApproval && !await approval.ApproveAsync(call, ct))
            return new ToolResult(call.Id, "The user denied this tool call.", true);
        try
        {
            var output = await tool.ExecuteAsync(call.Input!.Value, ct);
            if (output.Length > _opts.MaxToolOutputChars)
                output = output[.._opts.MaxToolOutputChars] + "\n[truncated]";
            return new ToolResult(call.Id, output, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new ToolResult(call.Id, $"Tool failed: {ex.Message}", true);
        }
    }
}

// ---------------- Fake tools ----------------

public sealed class ReadTerminalTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new("read_terminal",
        "Read the last N lines of the user's terminal session.",
        JsonDocument.Parse("""
        {"type":"object","properties":{
           "session":{"type":"string","description":"Terminal session id"},
           "lines":{"type":"integer","description":"Number of lines","minimum":1,"maximum":2000}},
         "required":["session","lines"],"additionalProperties":false}
        """).RootElement.Clone());
    public bool RequiresApproval => false;
    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct) => Task.FromResult(
        "$ systemctl status nginx\n● nginx.service - A high performance web server\n   Active: failed (Result: exit-code)\nnginx: [emerg] bind() to 0.0.0.0:80 failed (98: Address already in use)");
}

public sealed class RunCommandTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new("run_command",
        "Run a shell command on a host via the exec channel. Destructive: requires user approval.",
        JsonDocument.Parse("""
        {"type":"object","properties":{
           "host":{"type":"string"},
           "command":{"type":"string"},
           "timeout_s":{"type":"integer","minimum":1,"maximum":600}},
         "required":["host","command","timeout_s"],"additionalProperties":false}
        """).RootElement.Clone());
    public bool RequiresApproval => true;
    public List<string> Executed { get; } = [];
    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var cmd = input.GetProperty("command").GetString()!;
        lock (Executed) Executed.Add(cmd);
        return Task.FromResult($"exit=0\n(fake output of `{cmd}`)");
    }
}
