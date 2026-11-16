using MySSH.Core;
using static MySSH.Tools.PolicyVerdict;

namespace MySSH.Tools.Tests;

/// <summary>
/// P3 acceptance criterion (design §12: 策略矩阵测试通过): every AiPolicy × every tool/capability × the interesting
/// argument variants (allowlisted / not / high-risk / chained / overwrite / delete / execute / line break) → verdict.
/// The same table is checked against <see cref="PolicyEngine"/> directly AND end to end through <see cref="ToolInvoker"/>.
/// </summary>
public class PolicyMatrixTests
{
    /// <summary>One row: a tool call variant and the expected verdict (+ high-risk flag) per policy Off/ReadOnly/Ask/Trusted/FullAccess.</summary>
    public sealed record Scenario(
        string Name, string Tool, string? Command = null, bool Overwrite = false, bool Execute = false,
        IReadOnlyList<string>? Allowlist = null,
        (PolicyVerdict Verdict, bool HighRisk)[]? Expected = null)
    {
        public override string ToString() => Name;
    }

    /// <summary>FullAccess always allows (high-risk is only flagged), so it is not a parameter.</summary>
    private static (PolicyVerdict, bool)[] V(PolicyVerdict off, PolicyVerdict ro, PolicyVerdict ask, PolicyVerdict trusted, bool highRisk = false) =>
        [(off, false), (ro, highRisk), (ask, highRisk), (trusted, highRisk), (Allow, highRisk)];

    private static readonly AiPolicy[] s_policies = [AiPolicy.Off, AiPolicy.ReadOnly, AiPolicy.Ask, AiPolicy.Trusted, AiPolicy.FullAccess];

    public static readonly Scenario[] Scenarios =
    [
        // Read
        new("list_hosts", ToolNames.ListHosts, Expected: V(Deny, Allow, Allow, Allow)),
        new("sftp_list", ToolNames.SftpList, Expected: V(Deny, Allow, Allow, Allow)),
        new("sftp_stat", ToolNames.SftpStat, Expected: V(Deny, Allow, Allow, Allow)),
        new("sftp_read", ToolNames.SftpRead, Expected: V(Deny, Allow, Allow, Allow)),
        // TerminalRead
        new("list_sessions", ToolNames.ListSessions, Expected: V(Deny, Allow, Allow, Allow)),
        new("read_terminal", ToolNames.ReadTerminal, Expected: V(Deny, Allow, Allow, Allow)),
        // Execute
        new("run_command built-in allowlisted (uptime)", ToolNames.RunCommand, "uptime", Expected: V(Deny, Allow, Ask, Allow)),
        new("run_command built-in allowlisted with args (df -h /)", ToolNames.RunCommand, "df -h /", Expected: V(Deny, Allow, Ask, Allow)),
        new("run_command built-in two-word prefix (systemctl status nginx)", ToolNames.RunCommand, "systemctl status nginx", Expected: V(Deny, Allow, Ask, Allow)),
        new("run_command custom allowlisted", ToolNames.RunCommand, "df -h", Allowlist: ["df -h", "cat /etc/os-release"], Expected: V(Deny, Allow, Ask, Allow)),
        new("run_command not in custom allowlist (built-in no longer applies)", ToolNames.RunCommand, "uptime", Allowlist: ["df -h"], Expected: V(Deny, Deny, Ask, Allow)),
        new("run_command not allowlisted", ToolNames.RunCommand, "apt-get update", Expected: V(Deny, Deny, Ask, Allow)),
        new("run_command allowlisted verb but different subcommand (systemctl restart)", ToolNames.RunCommand, "systemctl restart nginx", Expected: V(Deny, Deny, Ask, Ask, highRisk: true)),
        new("run_command high-risk (rm -rf)", ToolNames.RunCommand, "rm -rf /tmp/build", Expected: V(Deny, Deny, Ask, Ask, highRisk: true)),
        new("run_command high-risk via sudo", ToolNames.RunCommand, "sudo systemctl status nginx", Expected: V(Deny, Deny, Ask, Ask, highRisk: true)),
        new("run_command chained after allowlisted (ls; rm -rf /)", ToolNames.RunCommand, "ls; rm -rf /", Expected: V(Deny, Deny, Ask, Ask, highRisk: true)),
        new("run_command substitution in allowlisted (cat $(…))", ToolNames.RunCommand, "cat $(ls /etc)", Expected: V(Deny, Deny, Ask, Allow)),
        new("run_command follow forever (tail -f)", ToolNames.RunCommand, "tail -f /var/log/syslog", Expected: V(Deny, Deny, Ask, Allow)),
        new("run_command high-risk custom-allowlisted (still denied on ReadOnly)", ToolNames.RunCommand, "systemctl restart nginx", Allowlist: ["systemctl restart"], Expected: V(Deny, Deny, Ask, Ask, highRisk: true)),
        // Write
        new("sftp_write create", ToolNames.SftpWrite, Expected: V(Deny, Deny, Ask, Allow)),
        new("sftp_write overwrite", ToolNames.SftpWrite, Overwrite: true, Expected: V(Deny, Deny, Ask, Ask)),
        new("sftp_mkdir", ToolNames.SftpMkdir, Expected: V(Deny, Deny, Ask, Allow)),
        new("sftp_delete", ToolNames.SftpDelete, Expected: V(Deny, Deny, Ask, Ask)),
        // TerminalWrite
        new("send_to_terminal type only", ToolNames.SendToTerminal, "ls -la", Expected: V(Deny, Deny, Ask, Allow)),
        new("send_to_terminal type only, risky text (not run)", ToolNames.SendToTerminal, "rm -rf /", Expected: V(Deny, Deny, Ask, Allow)),
        new("send_to_terminal execute", ToolNames.SendToTerminal, "ls -la", Execute: true, Expected: V(Deny, Deny, Ask, Ask)),
        new("send_to_terminal execute high-risk", ToolNames.SendToTerminal, "sudo reboot", Execute: true, Expected: V(Deny, Deny, Ask, Ask, highRisk: true)),
    ];

    public static TheoryData<AiPolicy, Scenario> Matrix()
    {
        var data = new TheoryData<AiPolicy, Scenario>();
        foreach (var s in Scenarios)
            foreach (var p in s_policies)
                data.Add(p, s);
        return data;
    }

    private static readonly ToolCatalog s_catalog = new();

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Policy_engine_matrix(AiPolicy policy, Scenario s)
    {
        var host = new Host { Label = "h", Hostname = "h", AiPolicy = policy, AiCommandAllowlist = s.Allowlist ?? [] };
        var tool = s_catalog.Find(s.Tool)!;
        var d = PolicyEngine.Instance.Authorize(new PolicyRequest(tool, host, s.Command, s.Overwrite, s.Execute));
        var (verdict, highRisk) = s.Expected![Array.IndexOf(s_policies, policy)];
        Assert.True(verdict == d.Verdict, $"{policy} / {s.Name}: expected {verdict}, got {d.Verdict} ({d.Reason})");
        Assert.True(highRisk == d.HighRisk, $"{policy} / {s.Name}: expected HighRisk={highRisk}, got {d.HighRisk} ({d.Reason})");
        if (d.Verdict != Allow)
            Assert.False(string.IsNullOrWhiteSpace(d.Reason));
    }

    [Fact]
    public void Matrix_covers_every_tool_and_capability_and_every_policy()
    {
        Assert.Equal(s_catalog.Tools.Select(t => t.Name).Order(), Scenarios.Select(s => s.Tool).Distinct().Order());
        Assert.Equal(Enum.GetValues<ToolCapability>().Order(), Scenarios.Select(s => s_catalog.Find(s.Tool)!.Capability).Distinct().Order());
        Assert.Equal(Enum.GetValues<AiPolicy>().Order(), s_policies.Order());
        Assert.All(Scenarios, s => Assert.Equal(s_policies.Length, s.Expected!.Length));
    }

    [Theory]
    [InlineData(AiPolicy.Ask, ToolNames.RunCommand, "uptime", false, false, ApprovalReason.AskPolicy, null)]
    [InlineData(AiPolicy.Ask, ToolNames.RunCommand, "rm -rf /tmp/x", false, false, ApprovalReason.HighRisk, "rm -r")]
    [InlineData(AiPolicy.Trusted, ToolNames.RunCommand, "rm -rf /tmp/x", false, false, ApprovalReason.HighRisk, "rm -r")]
    [InlineData(AiPolicy.Trusted, ToolNames.SftpWrite, null, true, false, ApprovalReason.Overwrite, null)]
    [InlineData(AiPolicy.Trusted, ToolNames.SftpDelete, null, false, false, ApprovalReason.Delete, null)]
    [InlineData(AiPolicy.Trusted, ToolNames.SendToTerminal, "ls", false, true, ApprovalReason.TerminalExecute, null)]
    public void Ask_decisions_carry_a_machine_readable_reason(AiPolicy policy, string tool, string? command, bool overwrite, bool execute,
        ApprovalReason kind, string? rule)
    {
        var host = new Host { Label = "h", Hostname = "h", AiPolicy = policy };
        var d = PolicyEngine.Instance.Authorize(new PolicyRequest(s_catalog.Find(tool)!, host, command, overwrite, execute));
        Assert.Equal(Ask, d.Verdict);
        Assert.Equal(kind, d.Kind);
        Assert.Equal(rule, d.RiskRule);
    }

    [Fact]
    public void Full_access_never_asks_but_still_flags_high_risk()
    {
        var host = new Host { Label = "h", Hostname = "h", AiPolicy = AiPolicy.FullAccess };
        var d = PolicyEngine.Instance.Authorize(new PolicyRequest(s_catalog.Find(ToolNames.RunCommand)!, host, "sudo systemctl stop nginx && rm -rf /var/www"));
        Assert.Equal(Allow, d.Verdict);
        Assert.True(d.HighRisk);
        Assert.Contains("FullAccess", d.Reason, StringComparison.Ordinal);
        Assert.Equal(PolicyDecision.Allowed, PolicyEngine.Instance.Authorize(new PolicyRequest(s_catalog.Find(ToolNames.SftpDelete)!, host)));
        Assert.Equal(PolicyDecision.Allowed, PolicyEngine.Instance.Authorize(new PolicyRequest(s_catalog.Find(ToolNames.SftpWrite)!, host, Overwrite: true)));
    }

    [Fact]
    public void Deny_reasons_name_the_allowlist()
    {
        var host = new Host { Label = "h", Hostname = "h", AiPolicy = AiPolicy.ReadOnly };
        var d = PolicyEngine.Instance.Authorize(new PolicyRequest(s_catalog.Find(ToolNames.RunCommand)!, host, "apt-get update"));
        Assert.Contains("built-in read-only allowlist", d.Reason, StringComparison.Ordinal);
        Assert.Contains("uptime", d.Reason, StringComparison.Ordinal);
        d = PolicyEngine.Instance.Authorize(new PolicyRequest(s_catalog.Find(ToolNames.RunCommand)!, host with { AiCommandAllowlist = ["df -h"] }, "uptime"));
        Assert.Contains("this host's allowlist (\"df -h\")", d.Reason, StringComparison.Ordinal);
        d = PolicyEngine.Instance.Authorize(new PolicyRequest(s_catalog.Find(ToolNames.RunCommand)!, host with { AiPolicy = AiPolicy.Trusted }, "rm -rf /"));
        Assert.Contains("rm -r", d.Reason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ end to end through the invoker

    public static TheoryData<AiPolicy, Scenario> InvokerMatrix() => Matrix();

    [Theory]
    [MemberData(nameof(InvokerMatrix))]
    public async Task Invoker_matrix_end_to_end(AiPolicy policy, Scenario s)
    {
        var rig = new Rig();
        var host = rig.AddHost("web", policy, s.Allowlist);
        var sftp = rig.Connector.SftpFor(host.Id);
        sftp.Put("/home/u/existing.txt", "old");
        sftp.Mkdir("/home/u/dir");
        rig.AddSession(host);
        var (verdict, highRisk) = s.Expected![Array.IndexOf(s_policies, policy)];

        var args = s.Tool switch
        {
            ToolNames.ListHosts or ToolNames.ListSessions => T.Args(),
            ToolNames.RunCommand => T.Args(("host", "web"), ("command", s.Command)),
            ToolNames.SftpList => T.Args(("host", "web"), ("path", "/home/u")),
            ToolNames.SftpStat or ToolNames.SftpRead => T.Args(("host", "web"), ("path", "existing.txt")),
            ToolNames.SftpWrite => T.Args(("host", "web"), ("path", s.Overwrite ? "existing.txt" : "new.txt"), ("content", "new"), ("mode", s.Overwrite ? "overwrite" : "create")),
            ToolNames.SftpMkdir => T.Args(("host", "web"), ("path", "newdir")),
            ToolNames.SftpDelete => T.Args(("host", "web"), ("path", "existing.txt")),
            ToolNames.ReadTerminal => T.Args(("session", "s1")),
            ToolNames.SendToTerminal => T.Args(("session", "s1"), ("text", s.Command), ("execute", s.Execute)),
            _ => throw new InvalidOperationException(s.Tool),
        };
        var r = await rig.CallAsync(s.Tool, args);
        var audit = rig.Audit.Single();
        Assert.Equal(s.Tool, audit.Tool);

        if (s.Tool is ToolNames.ListHosts or ToolNames.ListSessions)
        {
            // Not host-specific: always allowed, an Off host is simply not listed.
            Assert.False(r.IsError, r.Text);
            Assert.Equal(AuditDecision.Allowed, audit.Decision);
            Assert.Equal(policy != AiPolicy.Off, r.Structured!.Value.GetRawText().Contains(host.Id.ToString(), StringComparison.Ordinal));
            return;
        }

        switch (verdict)
        {
            case Deny when policy == AiPolicy.Off:
                Assert.True(r.IsError);
                Assert.StartsWith(s.Tool is ToolNames.ReadTerminal or ToolNames.SendToTerminal ? "Unknown terminal session" : "Unknown host", r.Text, StringComparison.Ordinal);
                Assert.Equal(AuditDecision.Invalid, audit.Decision);
                Assert.Null(audit.HostId);
                break;
            case Deny:
                Assert.True(r.IsError);
                Assert.Equal(AuditDecision.PolicyRejected, audit.Decision);
                Assert.Equal(host.Id, audit.HostId);
                Assert.Empty(rig.Approval.Requests);
                Assert.Empty(rig.Connector.Connects);
                Assert.Empty(rig.Terminals.Sent);
                break;
            case Ask:
                Assert.False(r.IsError, r.Text);
                Assert.Equal(AuditDecision.Approved, audit.Decision);
                var req = Assert.Single(rig.Approval.Requests);
                Assert.Equal(highRisk, req.HighRisk);
                Assert.Equal(host.Id, req.Host.Id);
                Assert.Equal(s.Tool, req.Tool.Name);
                break;
            case Allow:
                Assert.False(r.IsError, r.Text);
                Assert.Equal(AuditDecision.Allowed, audit.Decision);
                Assert.Empty(rig.Approval.Requests);
                break;
        }
        if (!r.IsError && s.Tool is ToolNames.RunCommand or ToolNames.SftpList or ToolNames.SftpRead or ToolNames.SftpWrite)
            Assert.All(rig.Connector.Connects, c => Assert.Equal(ConnectionOrigin.Mcp, c.Origin));
    }

    [Theory]
    [InlineData(AiPolicy.Trusted, "echo hi\n", false, true)]       // newline → treated as execute → Ask
    [InlineData(AiPolicy.Trusted, "echo hi\r", false, true)]
    [InlineData(AiPolicy.Trusted, "echo a\necho b", false, true)]
    [InlineData(AiPolicy.Trusted, "echo hi", false, false)]
    [InlineData(AiPolicy.Trusted, "echo hi", true, true)]
    [InlineData(AiPolicy.Ask, "echo hi", false, true)]
    public async Task Send_to_terminal_line_breaks_count_as_execute(AiPolicy policy, string text, bool execute, bool expectAsk)
    {
        var rig = new Rig();
        var host = rig.AddHost("web", policy);
        rig.AddSession(host);
        var r = await rig.CallAsync(ToolNames.SendToTerminal, ("session", "s1"), ("text", text), ("execute", execute));
        Assert.False(r.IsError, r.Text);
        Assert.Equal(expectAsk ? 1 : 0, rig.Approval.Requests.Count);
        if (expectAsk && text.Contains('\n', StringComparison.Ordinal) && text.TrimEnd('\n').Contains('\n', StringComparison.Ordinal))
            Assert.Contains("contains 2 lines", rig.Approval.Requests.Single().Summary, StringComparison.Ordinal);
        var sent = Assert.Single(rig.Terminals.Sent);
        Assert.Equal((text, execute), (sent.Text, sent.Execute));
    }

    [Fact]
    public async Task Send_to_terminal_multiline_high_risk_is_flagged()
    {
        var rig = new Rig();
        var host = rig.AddHost("web", AiPolicy.Trusted);
        rig.AddSession(host);
        await rig.CallAsync(ToolNames.SendToTerminal, ("session", "s1"), ("text", "cd /tmp\nrm -rf build\n"));
        var req = Assert.Single(rig.Approval.Requests);
        Assert.True(req.HighRisk);
        Assert.Contains("contains 2 lines", req.Summary, StringComparison.Ordinal);
        Assert.Contains("rm -r", req.Reason, StringComparison.Ordinal);
    }
}
