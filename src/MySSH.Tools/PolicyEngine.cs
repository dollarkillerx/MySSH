using MySSH.Core;

namespace MySSH.Tools;

/// <summary>
/// Per-host authorization (design §6.3). Shared by MCP and the AI assistant.
/// <code>
///              Read / TerminalRead   Execute (run_command)              Write (sftp_*)            TerminalWrite (send_to_terminal)
/// Off          Deny (host is hidden; the invoker reports "unknown host")
/// ReadOnly     Allow                 Allow if allowlisted, else Deny    Deny                      Deny
/// Ask          Allow                 Ask                                Ask                       Ask
/// Trusted      Allow                 Allow; Ask if high-risk            Allow; Ask for overwrite  Allow; Ask when execute=true
///                                                                       and for every delete      (or the text has a line break)
/// FullAccess   Allow                 Allow (high-risk is flagged only)  Allow                     Allow
/// </code>
/// High-risk rules (<see cref="HighRiskRules"/>) also set <see cref="PolicyDecision.HighRisk"/> on Ask decisions so the
/// approval prompt can highlight them. They are a safety net, not a security boundary.
/// </summary>
public sealed class PolicyEngine : IPolicyEngine
{
    public static readonly PolicyEngine Instance = new();

    public PolicyDecision Authorize(PolicyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Host;
        var tool = request.Tool;
        var policy = host.AiPolicy;

        if (policy == AiPolicy.Off || !Enum.IsDefined(policy))
            return new(PolicyVerdict.Deny, "This host is not available to AI tools.");

        if (tool.Capability is ToolCapability.Read or ToolCapability.TerminalRead)
            return PolicyDecision.Allowed;

        var isDelete = tool.Name == ToolNames.SftpDelete;
        // send_to_terminal only runs something when Enter is pressed (execute=true or a line break in the text).
        var runs = tool.Capability switch
        {
            ToolCapability.Execute => true,
            ToolCapability.TerminalWrite => request.Execute,
            _ => false,
        };
        var risk = runs && !string.IsNullOrWhiteSpace(request.Command) ? HighRiskRules.Match(request.Command) : null;
        var riskText = risk is null ? null : $"matches the high-risk rule \"{risk.Rule}\" ({risk.Description})";

        switch (policy)
        {
            case AiPolicy.ReadOnly:
                switch (tool.Capability)
                {
                    case ToolCapability.Execute:
                        var check = ReadOnlyCommandPolicy.Check(request.Command ?? "", host.AiCommandAllowlist);
                        return check.Allowed ? PolicyDecision.Allowed : new(PolicyVerdict.Deny, check.Reason, risk is not null);
                    case ToolCapability.Write:
                        return new(PolicyVerdict.Deny, "The host's AI policy is ReadOnly: file changes (write, mkdir, delete) are not allowed. Ask the user to switch the host to Ask or Trusted.", risk is not null);
                    case ToolCapability.TerminalWrite:
                        return new(PolicyVerdict.Deny, "The host's AI policy is ReadOnly: typing into the user's terminal is not allowed.", risk is not null);
                }
                break;

            case AiPolicy.Ask:
                var what = tool.Capability switch
                {
                    ToolCapability.Execute => "every command",
                    ToolCapability.Write => "every file change",
                    _ => "typing into the terminal",
                };
                return new(PolicyVerdict.Ask, riskText is null
                    ? $"The host's AI policy is Ask: {what} needs the user's approval."
                    : $"The host's AI policy is Ask and the command {riskText}.", risk is not null,
                    risk is null ? ApprovalReason.AskPolicy : ApprovalReason.HighRisk, risk?.Rule);

            case AiPolicy.Trusted:
                if (risk is not null)
                    return new(PolicyVerdict.Ask, $"The command {riskText}; high-risk commands always need approval, even on Trusted hosts.", true,
                        ApprovalReason.HighRisk, risk.Rule);
                if (tool.Capability == ToolCapability.Write && request.Overwrite)
                    return new(PolicyVerdict.Ask, "Overwriting an existing file always needs the user's approval.", Kind: ApprovalReason.Overwrite);
                if (tool.Capability == ToolCapability.Write && isDelete)
                    return new(PolicyVerdict.Ask, "Deleting files always needs the user's approval.", Kind: ApprovalReason.Delete);
                if (tool.Capability == ToolCapability.TerminalWrite && request.Execute)
                    return new(PolicyVerdict.Ask, "Running a command in the user's terminal (pressing Enter) always needs the user's approval.",
                        Kind: ApprovalReason.TerminalExecute);
                return PolicyDecision.Allowed;

            case AiPolicy.FullAccess:
                // The user handed this host over completely: no approvals at all. High-risk matches are still flagged
                // (audit / logs), never asked.
                return risk is null ? PolicyDecision.Allowed
                    : new(PolicyVerdict.Allow, $"The command {riskText}; allowed because the host's AI policy is FullAccess.", true, RiskRule: risk.Rule);
        }
        return new(PolicyVerdict.Deny, "Unsupported tool capability.");
    }
}
