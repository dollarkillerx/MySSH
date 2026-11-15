using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MySSH.Spike.McpHost;

public sealed record HostInfo(string Name, string Address, int Port, string[] Tags, string Policy);

/// <summary>
/// Demo tools. In production these would be thin adapters generated from MySSH.Tools.ToolCatalog.
/// The SDK creates one instance per invocation via ActivatorUtilities (ctor injection from DI).
/// </summary>
[McpServerToolType]
public sealed class MySshTools(IApprovalService approval, EventLog events, SpikeOptions options)
{
    private static readonly HostInfo[] Hosts =
    [
        new("web-01", "10.0.0.11", 22, ["prod", "web"], "Ask"),
        new("db-01", "10.0.0.21", 22, ["prod", "db"], "ReadOnly"),
        new("lab-pi", "192.168.1.50", 2222, ["lab"], "Trusted"),
    ];

    [McpServerTool(Name = "list_hosts", Title = "List SSH hosts", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Lists SSH hosts that are exposed to AI (aiPolicy != Off). Never returns credentials.")]
    public HostInfo[] ListHosts(
        [Description("Optional case-insensitive substring filter on host name.")] string? query = null)
    {
        events.Write("TOOL list_hosts", $"query={query ?? "<null>"}");
        return query is null ? Hosts : Hosts.Where(h => h.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    [McpServerTool(Name = "run_command", Title = "Run a shell command on a host", Destructive = true, ReadOnly = false, Idempotent = false, OpenWorld = true)]
    [Description("Runs a command on the given host via an SSH exec channel. Requires user approval.")]
    public async Task<CallToolResult> RunCommand(
        McpServer server,
        [Description("Host name as returned by list_hosts.")] string host,
        [Description("Shell command line to execute.")] string command,
        CancellationToken cancellationToken)
    {
        var client = server.ClientInfo is { } ci ? $"{ci.Name}/{ci.Version}" : "<unknown>";
        var tag = $"host={host} command=\"{command}\" client={client} session={server.SessionId ?? "<none>"} proto={server.NegotiatedProtocolVersion}";
        events.Write("TOOL run_command BEGIN", tag);
        // Dispose the registration when the handler returns: the HTTP transport cancels/disposes its linked
        // token AFTER a successful response too, which would otherwise look like a spurious cancellation.
        using var reg = cancellationToken.Register(() => events.Write("TOKEN CANCELLED", tag));
        var sw = Stopwatch.StartNew();
        try
        {
            if (Hosts.All(h => h.Name != host))
                return Error($"unknown host '{host}'");

            var decision = await approval.RequestAsync(new ApprovalRequest(client, host, command), cancellationToken);
            events.Write("APPROVAL", $"{decision} after {sw.ElapsedMilliseconds}ms {tag}");
            if (decision != ApprovalDecision.Allow)
                return Error($"run_command denied ({decision}) for {host}: {command}");

            await Task.Delay(options.RunDelay, cancellationToken); // fake exec; MUST observe the token
            events.Write("TOOL run_command END", $"ok after {sw.ElapsedMilliseconds}ms {tag}");
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"[fake] {host}$ {command}\nexit_status=0\nstdout=hello from {host}\n" }],
            };
        }
        catch (OperationCanceledException)
        {
            events.Write("TOOL run_command CANCELLED", $"after {sw.ElapsedMilliseconds}ms {tag}");
            throw;
        }
    }

    private static CallToolResult Error(string msg) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = msg }],
    };
}
