using System.Text.Json;
using MySSH.Core;
using MySSH.Ssh;
using MySSH.Ssh.IntegrationTests;

namespace MySSH.Tools.Tests;

/// <summary>
/// The real tool layer (ToolInvoker → SshConnector → SSH.NET) against a throwaway local sshd
/// (tests/MySSH.Ssh.IntegrationTests/SshdEnvironment.cs). Skips when no OpenSSH server is installed.
/// </summary>
[Collection(SshdGroup.Name)]
public sealed class EndToEndTests(SshdEnvironment env)
{
    sealed class Rig : IAsyncDisposable
    {
        public required Lab Lab { get; init; }
        public required Host Host { get; init; }
        public required ToolInvoker Invoker { get; init; }
        public required FakeApproval Approval { get; init; }
        public required MemAudit Audit { get; init; }

        public async Task<ToolCallResult> CallAsync(string tool, params (string, object?)[] args) =>
            await Invoker.InvokeAsync(new ToolCallRequest(tool, T.Args(args)), T.Mcp, T.Ct);

        public ValueTask DisposeAsync() => Lab.DisposeAsync();
    }

    /// <summary>A host with the given policy whose host key was accepted once from the UI (MCP itself never trusts keys).</summary>
    async Task<Rig> RigAsync(AiPolicy policy, bool trustHostKey = true)
    {
        env.RequireSshd();
        var lab = new Lab(env);
        var host = lab.AddHost("target");
        host = lab.Hosts.Add(host with { AiPolicy = policy });
        var connector = lab.Connector();
        if (trustHostKey)
            await using (await connector.ConnectAsync(host.Id, ConnectionOrigin.User, T.Ct)) { }
        var approval = new FakeApproval();
        var audit = new MemAudit();
        var invoker = new ToolInvoker(lab.Hosts, connector, PolicyEngine.Instance, approval, audit, NoTerminalSessions.Instance,
            new ToolOptions(), TimeProvider.System, null);
        return new Rig { Lab = lab, Host = host, Invoker = invoker, Approval = approval, Audit = audit };
    }

    string Dir(string name)
    {
        var d = Path.Combine(env.Data, "tools-" + name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact(Timeout = 60_000)]
    public async Task Run_command_returns_output_and_exit_code_and_is_audited()
    {
        await using var rig = await RigAsync(AiPolicy.Trusted);
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "target"), ("command", "echo hello-mcp; echo oops >&2; exit 3"));
        Assert.False(r.IsError, r.Text);
        Assert.Contains("hello-mcp", r.Text, StringComparison.Ordinal);
        Assert.Contains("oops", r.Text, StringComparison.Ordinal);
        var structured = r.Structured!.Value;
        Assert.Equal(3, structured.GetProperty("exitCode").GetInt32());
        var audit = rig.Audit.Single();
        Assert.Equal(AuditDecision.Allowed, audit.Decision);
        Assert.Equal(3, audit.ExitCode);
        Assert.Equal("mcp:test-client", audit.Actor);
        Assert.Empty(rig.Approval.Requests);
    }

    [Fact(Timeout = 60_000)]
    public async Task Untrusted_host_key_fails_for_mcp_without_prompting()
    {
        await using var rig = await RigAsync(AiPolicy.Trusted, trustHostKey: false);
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "target"), ("command", "true"));
        Assert.True(r.IsError);
        Assert.Contains("host key", r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Lab.Verifier.Checks); // strict origin: the UI verifier is never asked
    }

    [Fact(Timeout = 90_000)]
    public async Task Sftp_write_read_list_mkdir_and_delete_on_a_trusted_host()
    {
        await using var rig = await RigAsync(AiPolicy.Trusted);
        var dir = Dir("sftp");
        var file = Path.Combine(dir, "notes.txt");

        var w = await rig.CallAsync(ToolNames.SftpWrite, ("host", "target"), ("path", file), ("content", "第一行\nline 2\n"), ("mode", "create"));
        Assert.False(w.IsError, w.Text);
        Assert.Equal("第一行\nline 2\n", await File.ReadAllTextAsync(file, T.Ct));
        Assert.Empty(rig.Approval.Requests); // create on Trusted: no approval

        var again = await rig.CallAsync(ToolNames.SftpWrite, ("host", "target"), ("path", file), ("content", "x"), ("mode", "create"));
        Assert.True(again.IsError); // create never overwrites

        var read = await rig.CallAsync(ToolNames.SftpRead, ("host", "target"), ("path", file));
        Assert.Contains("第一行", read.Text, StringComparison.Ordinal);

        Assert.False((await rig.CallAsync(ToolNames.SftpMkdir, ("host", "target"), ("path", Path.Combine(dir, "sub")))).IsError);
        var list = await rig.CallAsync(ToolNames.SftpList, ("host", "target"), ("path", dir));
        Assert.Contains("notes.txt", list.Text, StringComparison.Ordinal);
        Assert.Contains("sub", list.Text, StringComparison.Ordinal);

        rig.Approval.Outcome = ApprovalOutcome.Denied; // delete always asks on Trusted
        var denied = await rig.CallAsync(ToolNames.SftpDelete, ("host", "target"), ("path", file));
        Assert.True(denied.IsError);
        Assert.True(File.Exists(file));
        rig.Approval.Outcome = ApprovalOutcome.Approved;
        Assert.False((await rig.CallAsync(ToolNames.SftpDelete, ("host", "target"), ("path", file))).IsError);
        Assert.False(File.Exists(file));
        Assert.Equal(2, rig.Approval.Requests.Count);
        Assert.Contains(rig.Audit.Entries, e => e.Tool == ToolNames.SftpDelete && e.Decision == AuditDecision.Denied);
        Assert.Contains(rig.Audit.Entries, e => e.Tool == ToolNames.SftpDelete && e.Decision == AuditDecision.Approved);
        Assert.DoesNotContain(rig.Audit.Entries, e => e.ArgumentsJson.Contains("第一行", StringComparison.Ordinal)); // content redacted
    }

    [Fact(Timeout = 60_000)]
    public async Task Read_only_host_runs_allowlisted_commands_and_never_connects_for_the_rest()
    {
        await using var rig = await RigAsync(AiPolicy.ReadOnly);
        var dir = Dir("ro");
        var marker = Path.Combine(dir, "should-not-exist");
        var ok = await rig.CallAsync(ToolNames.RunCommand, ("host", "target"), ("command", "uptime"));
        Assert.False(ok.IsError, ok.Text);

        var denied = await rig.CallAsync(ToolNames.RunCommand, ("host", "target"), ("command", $"touch {marker}"));
        Assert.True(denied.IsError);
        var chained = await rig.CallAsync(ToolNames.RunCommand, ("host", "target"), ("command", $"uptime; touch {marker}"));
        Assert.True(chained.IsError);
        Assert.False(File.Exists(marker));
        Assert.Equal(2, rig.Audit.Entries.Count(e => e.Decision == AuditDecision.PolicyRejected));
    }

    [Fact(Timeout = 60_000)]
    public async Task Ask_host_runs_nothing_when_the_user_denies()
    {
        await using var rig = await RigAsync(AiPolicy.Ask);
        var marker = Path.Combine(Dir("ask"), "created");
        rig.Approval.Outcome = ApprovalOutcome.Denied;
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "target"), ("command", $"touch {marker}"));
        Assert.True(r.IsError);
        Assert.False(File.Exists(marker));
        var req = Assert.Single(rig.Approval.Requests);
        Assert.Contains("touch", req.Summary, StringComparison.Ordinal);

        rig.Approval.Outcome = ApprovalOutcome.Approved;
        Assert.False((await rig.CallAsync(ToolNames.RunCommand, ("host", "target"), ("command", $"touch {marker}"))).IsError);
        Assert.True(File.Exists(marker));
    }

    [Fact(Timeout = 60_000)]
    public async Task Full_access_host_runs_high_risk_commands_without_asking()
    {
        await using var rig = await RigAsync(AiPolicy.FullAccess);
        var dir = Dir("full");
        await File.WriteAllTextAsync(Path.Combine(dir, "f"), "x", T.Ct);
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "target"), ("command", $"rm -rf {dir}"));
        Assert.False(r.IsError, r.Text);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(rig.Approval.Requests);
        Assert.Equal(AuditDecision.Allowed, rig.Audit.Single().Decision);
    }

    [Fact(Timeout = 60_000)]
    public async Task Hidden_host_is_reported_as_unknown()
    {
        await using var rig = await RigAsync(AiPolicy.Off, trustHostKey: false);
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "target"), ("command", "true"));
        Assert.True(r.IsError);
        Assert.Contains("unknown host", r.Text, StringComparison.OrdinalIgnoreCase);
        var hosts = await rig.CallAsync(ToolNames.ListHosts);
        Assert.DoesNotContain("target", JsonSerializer.Serialize(hosts.Structured), StringComparison.Ordinal);
    }
}
