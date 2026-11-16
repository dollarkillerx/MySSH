using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace MySSH.Mcp;

/// <summary>Static prompt templates (design §6.4). They only steer the model; every action still goes through the tools.</summary>
internal static class McpPrompts
{
    public const string DiagnoseHost = "diagnose_host";
    public const string AnalyzeLogs = "analyze_logs";

    public static readonly List<Prompt> All =
    [
        new Prompt
        {
            Name = DiagnoseHost,
            Title = "Diagnose a host",
            Description = "Read-only health check of one MySSH host: load, memory, disk, failed services, recent errors.",
            Arguments = [new PromptArgument { Name = "host", Description = "Host name or id as returned by list_hosts.", Required = true }],
        },
        new Prompt
        {
            Name = AnalyzeLogs,
            Title = "Analyze a log file",
            Description = "Read a log file (or directory of logs) on a MySSH host and summarize errors and patterns.",
            Arguments =
            [
                new PromptArgument { Name = "host", Description = "Host name or id as returned by list_hosts.", Required = true },
                new PromptArgument { Name = "path", Description = "Absolute path of the log file or directory, e.g. /var/log/nginx/error.log.", Required = true },
            ],
        },
    ];

    public static GetPromptResult Get(GetPromptRequestParams? p)
    {
        var name = p?.Name ?? throw new McpProtocolException("missing params", McpErrorCode.InvalidParams);
        string text;
        string description;
        switch (name)
        {
            case DiagnoseHost:
            {
                var host = Arg(p, "host");
                description = $"Diagnose {host}";
                text =
                    $"Diagnose the health of the MySSH host \"{host}\" without changing anything on it.\n\n" +
                    "1. Call list_hosts and confirm the host exists; use its exact name/id from the result.\n" +
                    "2. Prefer read-only tools first: sftp_stat / sftp_read for files such as /etc/os-release, /var/log/syslog " +
                    "or /var/log/messages (read only the tail with max_bytes).\n" +
                    "3. When you need live data, use run_command with non-modifying commands only, one purpose per call, e.g. " +
                    "`uptime`, `df -h`, `free -m`, `ps aux --sort=-%cpu | head -15`, `systemctl --failed --no-pager`, " +
                    "`journalctl -p err -n 50 --no-pager`. Each command may need the user's approval in MySSH; if one is " +
                    "denied, do not retry it — continue with what you have.\n" +
                    "4. Do not restart services, edit, write or delete files.\n\n" +
                    "Finish with a short report: overall status, findings ordered by severity with the evidence, and suggested " +
                    "fixes as commands for the user to review (do not run them).";
                break;
            }
            case AnalyzeLogs:
            {
                var host = Arg(p, "host");
                var path = Arg(p, "path");
                description = $"Analyze {path} on {host}";
                text =
                    $"Analyze the log at \"{path}\" on the MySSH host \"{host}\" without changing anything.\n\n" +
                    "1. Call list_hosts and confirm the host exists.\n" +
                    $"2. Call sftp_stat on \"{path}\". If it is a directory, sftp_list it and pick the most recent / relevant " +
                    "log files (including rotated ones such as .1 or .gz only if needed).\n" +
                    "3. Read with sftp_read, starting from the end of large files (use max_bytes); read more only if the " +
                    "first chunk is not enough.\n" +
                    "4. Use run_command (read-only commands such as `tail`, `grep -c`, `zcat … | tail`) only when sftp_read " +
                    "cannot do it; it may need the user's approval.\n\n" +
                    "Report: time range covered, error/warning counts, the top recurring messages with example lines, a " +
                    "timeline of notable events, likely root causes, and next steps for the user. Never write or delete files.";
                break;
            }
            default:
                throw new McpProtocolException($"Unknown prompt: '{name}'", McpErrorCode.InvalidParams);
        }
        return new GetPromptResult
        {
            Description = description,
            Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }],
        };
    }

    private static string Arg(GetPromptRequestParams p, string name)
    {
        if (p.Arguments is { } args && args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
            return s.Length > 512 ? s[..512] : s;
        throw new McpProtocolException($"Missing required argument '{name}' for prompt '{p.Name}'", McpErrorCode.InvalidParams);
    }
}
