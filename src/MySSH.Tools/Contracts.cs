using System.Text.Json;
using MySSH.Core;

namespace MySSH.Tools;

// Shared tool layer contracts (design §6). MCP (MySSH.Mcp) and the built-in AI assistant (MySSH.Ai, P4) both call tools
// through IToolInvoker; neither talks to MySSH.Ssh directly. The desktop app implements IApprovalService and
// ITerminalSessionRegistry.

/// <summary>What a tool does, for the per-host policy matrix (design §6.3).</summary>
public enum ToolCapability
{
    /// <summary>list_hosts, sftp_list/stat/read.</summary>
    Read = 0,
    /// <summary>read_terminal, list_sessions: reads what the user sees in an open terminal.</summary>
    TerminalRead = 1,
    /// <summary>run_command.</summary>
    Execute = 2,
    /// <summary>sftp_write/mkdir/delete.</summary>
    Write = 3,
    /// <summary>send_to_terminal.</summary>
    TerminalWrite = 4,
}

/// <summary>
/// A tool defined once for every front end. <see cref="InputSchema"/> is a JSON Schema object with
/// <c>additionalProperties: false</c>; MCP publishes it as-is, the AI assistant converts it per provider.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Title,
    string Description,
    JsonElement InputSchema,
    ToolCapability Capability,
    bool ReadOnly,
    bool Destructive,
    bool Idempotent,
    bool OpenWorld);

/// <summary>Who is calling. <see cref="Origin"/> is never <see cref="ConnectionOrigin.User"/> here.</summary>
/// <param name="Label">Audit actor, "mcp:&lt;client&gt;" or "ai:&lt;provider&gt;/&lt;model&gt;".</param>
/// <param name="DisplayName">Shown in approval prompts, e.g. "Claude Code (MCP)".</param>
public sealed record ToolActor(ConnectionOrigin Origin, string Label, string DisplayName);

public sealed record ToolCallRequest(string ToolName, JsonElement Arguments);

/// <param name="Text">Human/model readable result (or error message when <see cref="IsError"/>).</param>
/// <param name="Structured">Optional machine-readable result (MCP structuredContent).</param>
public sealed record ToolCallResult(bool IsError, string Text, JsonElement? Structured = null)
{
    public static ToolCallResult Error(string message) => new(true, message);
}

public interface IToolCatalog
{
    IReadOnlyList<ToolDefinition> Tools { get; }
    ToolDefinition? Find(string name);
}

/// <summary>
/// The single entry point: Validate(schema) → resolve host/session (hosts with AiPolicy Off are reported as unknown) →
/// PolicyEngine → approval when required → execute → truncate → audit. Never throws for tool-level failures; returns
/// <see cref="ToolCallResult.IsError"/> instead. Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/>
/// is cancelled (audited as Cancelled).
/// </summary>
public interface IToolInvoker
{
    Task<ToolCallResult> InvokeAsync(ToolCallRequest request, ToolActor actor, CancellationToken ct = default);
}

// ------------------------------------------------------------------ policy

public enum PolicyVerdict { Allow = 0, Ask = 1, Deny = 2 }

/// <summary>Why a call needs approval. <see cref="PolicyDecision.Reason"/> carries the same in English for the AI caller.</summary>
public enum ApprovalReason
{
    None = 0,
    /// <summary>The host's policy is Ask: every command / write / terminal input asks.</summary>
    AskPolicy = 1,
    /// <summary>A high-risk rule matched (<see cref="PolicyDecision.RiskRule"/>).</summary>
    HighRisk = 2,
    /// <summary>sftp_write replacing an existing file (always asks on Trusted).</summary>
    Overwrite = 3,
    /// <summary>sftp_delete (always asks on Trusted).</summary>
    Delete = 4,
    /// <summary>send_to_terminal pressing Enter (always asks on Trusted).</summary>
    TerminalExecute = 5,
}

/// <param name="Reason">Why Ask / Deny (shown in the approval prompt or returned to the caller).</param>
/// <param name="HighRisk">A high-risk rule matched (e.g. "rm -rf"); the prompt highlights it.</param>
/// <param name="Kind">Machine-readable reason for Ask, so the UI can explain it in the user's language.</param>
/// <param name="RiskRule">The matched high-risk rule (e.g. "rm -r"), when <paramref name="HighRisk"/>.</param>
public sealed record PolicyDecision(PolicyVerdict Verdict, string? Reason = null, bool HighRisk = false,
    ApprovalReason Kind = ApprovalReason.None, string? RiskRule = null)
{
    public static readonly PolicyDecision Allowed = new(PolicyVerdict.Allow);
}

/// <summary>What the policy needs to know about one call.</summary>
/// <param name="Command">run_command command line, or send_to_terminal text when <paramref name="Execute"/>.</param>
/// <param name="Overwrite">sftp_write replacing an existing file.</param>
/// <param name="Execute">send_to_terminal with execute=true (press Enter).</param>
public sealed record PolicyRequest(ToolDefinition Tool, Host Host, string? Command = null, bool Overwrite = false, bool Execute = false);

public interface IPolicyEngine
{
    PolicyDecision Authorize(PolicyRequest request);
}

// ------------------------------------------------------------------ approval

public enum ApprovalOutcome { Approved = 0, Denied = 1, TimedOut = 2, Cancelled = 3 }

/// <param name="Summary">One line: the command, "write /etc/nginx/nginx.conf (overwrite, 2.1 KB)", ...</param>
/// <param name="Details">Optional longer text (full command, file preview); may be truncated by the UI.</param>
public sealed record ApprovalRequest(
    Guid Id,
    ToolActor Actor,
    ToolDefinition Tool,
    Host Host,
    string Summary,
    string? Details,
    string? Reason,
    bool HighRisk,
    DateTimeOffset Deadline,
    ApprovalReason ReasonKind = ApprovalReason.None,
    string? RiskRule = null);

/// <summary>
/// Implemented by the GUI: MCP calls get a modal, AI calls an inline card (P4). Implementations must not block the calling
/// thread on the UI thread, must queue concurrent requests, and must return <see cref="ApprovalOutcome.Cancelled"/> when
/// <paramref name="ct"/> fires (the tool layer adds the 120 s deadline and maps it to TimedOut).
/// </summary>
public interface IApprovalService
{
    Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

// ------------------------------------------------------------------ terminal sessions (implemented by the desktop app)

/// <param name="Id">Stable for the lifetime of the tab.</param>
public sealed record TerminalSessionInfo(string Id, Guid HostId, string HostLabel, string Title, bool IsConnected, int Columns, int Rows);

public interface ITerminalSessionRegistry
{
    IReadOnlyList<TerminalSessionInfo> List();

    /// <summary>Last <paramref name="lines"/> lines of the screen + scrollback as plain text; null if the session is gone.</summary>
    Task<string?> ReadAsync(string sessionId, int lines, CancellationToken ct = default);

    /// <summary>
    /// Types <paramref name="text"/> into the session (bracketed paste when the shell asked for it). <paramref name="execute"/>
    /// appends Enter. Returns false if the session is gone or not connected.
    /// </summary>
    Task<bool> SendAsync(string sessionId, string text, bool execute, CancellationToken ct = default);
}

/// <summary>Used when the host app has no terminal UI (tests, headless): no sessions.</summary>
public sealed class NoTerminalSessions : ITerminalSessionRegistry
{
    public static readonly NoTerminalSessions Instance = new();
    public IReadOnlyList<TerminalSessionInfo> List() => [];
    public Task<string?> ReadAsync(string sessionId, int lines, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<bool> SendAsync(string sessionId, string text, bool execute, CancellationToken ct = default) => Task.FromResult(false);
}

// ------------------------------------------------------------------ options

public sealed record ToolOptions
{
    /// <summary>Unanswered approvals count as denied after this (design §6.3).</summary>
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan DefaultCommandTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan MaxCommandTimeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Per stream (stdout / stderr) for run_command.</summary>
    public int MaxCommandOutputBytes { get; init; } = 64 * 1024;
    public int DefaultSftpReadBytes { get; init; } = 256 * 1024;
    public int MaxSftpReadBytes { get; init; } = 1024 * 1024;
    public int MaxSftpWriteBytes { get; init; } = 1024 * 1024;
    public int MaxTerminalLines { get; init; } = 500;
    public int MaxSftpListEntries { get; init; } = 1000;
}
