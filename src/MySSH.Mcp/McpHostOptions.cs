using System.Security.Cryptography;

namespace MySSH.Mcp;

/// <summary>Configuration for <see cref="McpServerHost.StartAsync"/>. Both transports are independent switches.</summary>
public sealed record McpHostOptions
{
    /// <summary>Serve Streamable HTTP (stateless) on <c>http://127.0.0.1:{Port}/mcp</c>.</summary>
    public bool EnableHttp { get; init; } = true;

    /// <summary>TCP port on 127.0.0.1; 0 lets the OS pick one (read it back from <see cref="McpServerHost.Port"/> and persist it).</summary>
    public int Port { get; init; }

    /// <summary>
    /// Bearer token HTTP clients must send (<c>Authorization: Bearer &lt;token&gt;</c>). When null or empty a fresh one is
    /// generated (<see cref="McpToken.Generate"/>) and exposed as <see cref="McpServerHost.Token"/>.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>Serve the local socket (Unix domain socket / Windows named pipe) used by the <c>myssh-mcp</c> stdio bridge.</summary>
    public bool EnableLocalSocket { get; init; } = true;

    /// <summary>
    /// Overrides the default socket path (<see cref="McpServerHost.DefaultLocalSocketPath"/>); on Windows a pipe name or
    /// <c>\\.\pipe\name</c>. Mainly for tests.
    /// </summary>
    public string? LocalSocketPath { get; init; }

    public string ServerName { get; init; } = "myssh";
    public string ServerVersion { get; init; } = typeof(McpHostOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Sent to clients as MCP server instructions.</summary>
    public string? Instructions { get; init; } = DefaultInstructions;

    public const string DefaultInstructions =
        "MySSH gives you access to the SSH hosts the user enabled for AI/MCP access. Start with list_hosts. Prefer the " +
        "read-only tools (list_hosts, sftp_list, sftp_stat, sftp_read, list_sessions, read_terminal) before anything that " +
        "changes a host. run_command, sftp_write/mkdir/delete and send_to_terminal may require the user's approval in the " +
        "MySSH window, and a denied or timed-out approval is returned as an error: do not retry it in a loop.";
}

/// <summary>HTTP bearer tokens.</summary>
public static class McpToken
{
    /// <summary>32 random bytes, base64url without padding (43 characters).</summary>
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Thrown by <see cref="McpServerHost.StartAsync"/> when another process already serves the local MCP socket (or pipe):
/// another MySSH instance is running. The desktop app uses it for single-instance detection.
/// </summary>
public sealed class McpInstanceRunningException : Exception
{
    public McpInstanceRunningException() : this("another MySSH instance is already running") { }
    public McpInstanceRunningException(string message) : base(message) { }
    public McpInstanceRunningException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>The socket path or pipe address that is in use.</summary>
    public string? Address { get; init; }
}

public enum McpClientTransport { Http = 0, Local = 1 }

/// <summary>
/// One MCP client as seen by the host (snapshot). HTTP is stateless (design §6.4), so HTTP clients are aggregated per
/// client name: one entry per <c>clientInfo.name</c> sent by 2026-07-28 clients, and a single "http" entry for older
/// clients that do not identify themselves per request. Local socket clients are one entry per connection.
/// </summary>
/// <param name="Id">Stable key: <c>http:&lt;name&gt;</c>, <c>http</c>, or <c>local:&lt;n&gt;</c>. Pass it to <see cref="McpServerHost.DisconnectAsync"/>.</param>
/// <param name="Name">Client-reported name (self-asserted, sanitized); null when unknown.</param>
/// <param name="Version">Client-reported version; null when unknown.</param>
/// <param name="CallCount">Completed + running tool calls and resource reads.</param>
/// <param name="InFlightCalls">Tool calls / resource reads currently running (may be waiting for an approval).</param>
/// <param name="IsConnected">Local: the connection is open. HTTP: always false (there is no connection to hold on to).</param>
public sealed record McpClientInfo(
    string Id,
    McpClientTransport Transport,
    string? Name,
    string? Version,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int CallCount,
    int InFlightCalls,
    bool IsConnected)
{
    /// <summary>Human readable, e.g. "claude-code (MCP)"; also used as the approval prompt's actor.</summary>
    public string DisplayName => McpActors.DisplayName(Transport, Name);
}
