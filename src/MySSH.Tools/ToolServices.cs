using Microsoft.Extensions.Logging;
using MySSH.Core;
using MySSH.Ssh;

namespace MySSH.Tools;

/// <summary>The composed tool layer: hand <see cref="Catalog"/> to front ends for tool listings, call <see cref="Invoker"/>.</summary>
public sealed record ToolLayer(ToolCatalog Catalog, ToolInvoker Invoker, IPolicyEngine Policy, ToolOptions Options);

/// <summary>One-line composition for the desktop app (and tests).</summary>
public static class ToolServices
{
    /// <example><code>
    /// var tools = ToolServices.Create(storage.Hosts, connector, approvalService, storage.AuditLog, terminalRegistry);
    /// mcpServer.Use(tools.Catalog, tools.Invoker);
    /// </code></example>
    public static ToolLayer Create(
        IHostRepository hosts, ISshConnector connector, IApprovalService approvals, IAuditLogStore audit,
        ITerminalSessionRegistry? terminals = null, ToolOptions? options = null, TimeProvider? timeProvider = null,
        ILogger? logger = null, IPolicyEngine? policy = null)
    {
        options ??= new ToolOptions();
        policy ??= PolicyEngine.Instance;
        var invoker = new ToolInvoker(hosts, connector, policy, approvals, audit, terminals ?? NoTerminalSessions.Instance,
            options, timeProvider ?? TimeProvider.System, logger);
        return new ToolLayer(invoker.Catalog, invoker, policy, options);
    }
}
