namespace MySSH.Desktop.Services;

/// <param name="Transport">"http" or "local".</param>
/// <param name="CanDisconnect">Local-socket sessions can be closed; stateless HTTP has no session (rotate the token instead).</param>
public sealed record McpClientRow(string Id, string Transport, string Name, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long Calls, int InFlight, bool CanDisconnect);

/// <summary>
/// Owns the MCP server lifetime for the desktop app (design §6.4): off by default, started on the thread pool after unlock
/// when enabled, port and token persisted. All members are safe to call from the UI thread; <see cref="Changed"/> may be
/// raised on any thread.
/// </summary>
public interface IMcpController
{
    bool IsEnabled { get; }
    bool IsRunning { get; }
    /// <summary>Last start failure (port in use, another MySSH instance owns the socket, ...).</summary>
    string? StartError { get; }
    Uri? HttpEndpoint { get; }
    string? LocalSocketPath { get; }
    string? Token { get; }
    /// <summary>Path of the myssh-mcp stdio bridge next to the app, when present.</summary>
    string? BridgePath { get; }
    IReadOnlyList<McpClientRow> Clients { get; }
    event Action? Changed;

    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
    /// <summary>Invalidates the current token (existing HTTP clients must be reconfigured) and restarts the server.</summary>
    Task RegenerateTokenAsync(CancellationToken ct = default);
    Task DisconnectAsync(string clientId);

    string ClaudeCodeCommand { get; }
    string ClaudeDesktopJson { get; }
    string GenericHttpJson { get; }
}
