using System.Collections.Concurrent;
using System.Text.Json;
using MySSH.Core;
using MySSH.Tools;

namespace MySSH.Mcp.Tests;

/// <summary>Stand-in for the real ToolCatalog (MySSH.Tools): the tool names of design §6.2 with small schemas.</summary>
internal sealed class FakeCatalog : IToolCatalog
{
    public static readonly FakeCatalog Instance = new();

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public const string HostArg = """{"type":"object","properties":{"host":{"type":"string"}},"required":["host"],"additionalProperties":false}""";

    public IReadOnlyList<ToolDefinition> Tools { get; } =
    [
        new("list_hosts", "List hosts", "Lists hosts.", Schema("""{"type":"object","properties":{"query":{"type":"string"},"tag":{"type":"string"}},"additionalProperties":false}"""), ToolCapability.Read, ReadOnly: true, Destructive: false, Idempotent: true, OpenWorld: false),
        new("run_command", "Run command", "Runs a command.", Schema("""{"type":"object","properties":{"host":{"type":"string"},"command":{"type":"string"},"timeout_s":{"type":"integer","default":60}},"required":["host","command"],"additionalProperties":false}"""), ToolCapability.Execute, ReadOnly: false, Destructive: true, Idempotent: false, OpenWorld: true),
        new("sftp_read", "Read file", "Reads a file.", Schema("""{"type":"object","properties":{"host":{"type":"string"},"path":{"type":"string"}},"required":["host","path"],"additionalProperties":false}"""), ToolCapability.Read, ReadOnly: true, Destructive: false, Idempotent: true, OpenWorld: true),
        new("list_sessions", "List sessions", "Lists terminal sessions.", Schema("""{"type":"object","properties":{},"additionalProperties":false}"""), ToolCapability.TerminalRead, ReadOnly: true, Destructive: false, Idempotent: true, OpenWorld: false),
        new("read_terminal", "Read terminal", "Reads a terminal.", Schema("""{"type":"object","properties":{"session":{"type":"string"},"lines":{"type":"integer"}},"required":["session"],"additionalProperties":false}"""), ToolCapability.TerminalRead, ReadOnly: true, Destructive: false, Idempotent: true, OpenWorld: false),
        new("send_to_terminal", "Send to terminal", "Types into a terminal.", Schema("""{"type":"object","properties":{"session":{"type":"string"},"text":{"type":"string"},"execute":{"type":"boolean","default":false}},"required":["session","text"],"additionalProperties":false}"""), ToolCapability.TerminalWrite, ReadOnly: false, Destructive: true, Idempotent: false, OpenWorld: false),
        // test-only tools
        new("slow", "Slow", "Waits until cancelled or released.", Schema("""{"type":"object","properties":{"marker":{"type":"string"},"ms":{"type":"integer"}},"required":["marker"],"additionalProperties":false}"""), ToolCapability.Execute, ReadOnly: false, Destructive: true, Idempotent: false, OpenWorld: true),
        new("boom", "Boom", "Invoker throws.", Schema("""{"type":"object","properties":{},"additionalProperties":false}"""), ToolCapability.Read, ReadOnly: true, Destructive: false, Idempotent: true, OpenWorld: false),
    ];

    public ToolDefinition? Find(string name) => Tools.FirstOrDefault(t => t.Name == name);
}

internal sealed record InvokedCall(string Tool, JsonElement Arguments, ToolActor Actor);

/// <summary>
/// Stand-in for ToolInvoker. "slow" blocks until its CancellationToken fires (recorded in <see cref="Cancelled"/>) or
/// for "ms" milliseconds; "run_command" with host "nope" returns IsError; "boom" throws.
/// </summary>
internal sealed class FakeInvoker : IToolInvoker
{
    public ConcurrentQueue<InvokedCall> Calls { get; } = new();
    public ConcurrentDictionary<string, TaskCompletionSource> Started { get; } = new();
    public ConcurrentDictionary<string, TaskCompletionSource> Cancelled { get; } = new();
    public ConcurrentDictionary<string, bool> Completed { get; } = new();

    public TaskCompletionSource StartedFor(string marker) => Started.GetOrAdd(marker, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
    public TaskCompletionSource CancelledFor(string marker) => Cancelled.GetOrAdd(marker, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    public async Task<ToolCallResult> InvokeAsync(ToolCallRequest request, ToolActor actor, CancellationToken ct = default)
    {
        Calls.Enqueue(new InvokedCall(request.ToolName, request.Arguments.Clone(), actor));
        var args = request.Arguments;
        switch (request.ToolName)
        {
            case "list_hosts":
            {
                var structured = JsonDocument.Parse("""{"hosts":[{"name":"web-01","host":"10.0.0.1"},{"name":"db-01","host":"10.0.0.2"}]}""").RootElement.Clone();
                return new ToolCallResult(false, "web-01 10.0.0.1\ndb-01 10.0.0.2", structured);
            }
            case "run_command":
            {
                var host = args.GetProperty("host").GetString();
                if (host == "nope") return ToolCallResult.Error("unknown host 'nope'");
                return new ToolCallResult(false, $"[fake] {host}$ {args.GetProperty("command").GetString()} by {actor.Label}");
            }
            case "read_terminal":
            {
                var session = args.GetProperty("session").GetString();
                return session == "gone" ? ToolCallResult.Error("unknown session 'gone'") : new ToolCallResult(false, $"screen of {session}\n$ ");
            }
            case "slow":
            {
                var marker = args.GetProperty("marker").GetString()!;
                var ms = args.TryGetProperty("ms", out var m) ? m.GetInt32() : Timeout.Infinite;
                StartedFor(marker).TrySetResult();
                try
                {
                    await Task.Delay(ms, ct);
                }
                catch (OperationCanceledException)
                {
                    // G6: the HTTP transport also fires the token after a successful request; only count it while running.
                    CancelledFor(marker).TrySetResult();
                    throw;
                }
                Completed[marker] = true;
                return new ToolCallResult(false, $"slept {ms}ms");
            }
            case "boom":
                throw new InvalidOperationException("secret internal detail");
            default:
                return new ToolCallResult(false, $"ok {request.ToolName}");
        }
    }
}

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(20);
        }
    }
}

internal static class ActorExtensions
{
    public static bool IsMcp(this ToolActor a) => a.Origin == ConnectionOrigin.Mcp;
}
