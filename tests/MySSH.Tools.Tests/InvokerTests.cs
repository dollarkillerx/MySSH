using System.Text;
using System.Text.Json;
using MySSH.Core;
using MySSH.Ssh;

namespace MySSH.Tools.Tests;

public class InvokerTests
{
    // ------------------------------------------------------------------ host visibility & resolution

    [Fact]
    public async Task Off_host_looks_exactly_like_a_missing_host()
    {
        var rig = new Rig();
        var off = rig.AddHost("secret-db", AiPolicy.Off);
        var byLabelOff = await rig.CallAsync(ToolNames.RunCommand, ("host", "secret-db"), ("command", "uptime"));
        var byLabelMissing = await rig.CallAsync(ToolNames.RunCommand, ("host", "nothing-here"), ("command", "uptime"));
        var byIdOff = await rig.CallAsync(ToolNames.SftpList, ("host", off.Id.ToString()), ("path", "/"));
        var byIdMissing = await rig.CallAsync(ToolNames.SftpList, ("host", Guid.NewGuid().ToString()), ("path", "/"));

        Assert.True(byLabelOff.IsError && byLabelMissing.IsError && byIdOff.IsError && byIdMissing.IsError);
        Assert.Equal(byLabelMissing.Text.Replace("nothing-here", "X", StringComparison.Ordinal), byLabelOff.Text.Replace("secret-db", "X", StringComparison.Ordinal));
        Assert.Equal(ToolInvoker.UnknownHostMessage("X"), byLabelOff.Text.Replace("secret-db", "X", StringComparison.Ordinal));
        Assert.StartsWith("Unknown host '", byIdOff.Text, StringComparison.Ordinal);
        Assert.Empty(rig.Connector.Connects);
        Assert.Empty(rig.Approval.Requests);
        Assert.All(rig.Audit.Entries, e => Assert.Equal((AuditDecision.Invalid, (Guid?)null), (e.Decision, e.HostId)));
        Assert.Equal(4, rig.Audit.Entries.Count);

        var list = await rig.CallAsync(ToolNames.ListHosts);
        Assert.DoesNotContain("secret-db", list.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(off.Id.ToString(), list.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rdp_hosts_are_invisible_to_ai_tools_whatever_their_policy()
    {
        var rig = new Rig();
        var win = rig.AddHost("win-desktop", AiPolicy.FullAccess);
        rig.Hosts.Add(win with { Protocol = HostProtocol.Rdp, Port = Host.DefaultRdpPort });
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "win-desktop"), ("command", "dir"));
        Assert.True(r.IsError);
        Assert.Equal(ToolInvoker.UnknownHostMessage("win-desktop"), r.Text);
        Assert.Empty(rig.Connector.Connects);
        Assert.DoesNotContain("win-desktop", (await rig.CallAsync(ToolNames.ListHosts)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Off_host_sessions_are_hidden()
    {
        var rig = new Rig();
        var off = rig.AddHost("off", AiPolicy.Off);
        var on = rig.AddHost("on", AiPolicy.ReadOnly);
        rig.AddSession(off, "s-off");
        rig.AddSession(on, "s-on");
        var list = await rig.CallAsync(ToolNames.ListSessions);
        Assert.Contains("s-on", list.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("s-off", list.Text, StringComparison.Ordinal);
        var read = await rig.CallAsync(ToolNames.ReadTerminal, ("session", "s-off"));
        var missing = await rig.CallAsync(ToolNames.ReadTerminal, ("session", "s-zzz"));
        Assert.True(read.IsError);
        Assert.Equal(missing.Text.Replace("s-zzz", "X", StringComparison.Ordinal), read.Text.Replace("s-off", "X", StringComparison.Ordinal));
        var ok = await rig.CallAsync(ToolNames.ReadTerminal, ("session", "s-on"), ("lines", 2));
        Assert.False(ok.IsError, ok.Text);
        Assert.Equal("line2\n$ ", ok.Text);
    }

    [Fact]
    public async Task Labels_are_case_insensitive_and_ambiguity_lists_candidate_ids()
    {
        var rig = new Rig();
        var a = rig.AddHost("Web", AiPolicy.Trusted, hostname: "10.0.0.1");
        var b = rig.AddHost("web", AiPolicy.Trusted, hostname: "10.0.0.2");
        rig.AddHost("WEB", AiPolicy.Off, hostname: "10.0.0.3"); // hidden: not a candidate
        var c = rig.AddHost("db", AiPolicy.Trusted);

        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "WEB"), ("command", "uptime"));
        Assert.True(r.IsError);
        Assert.Contains("matches 2 hosts", r.Text, StringComparison.Ordinal);
        Assert.Contains(a.Id.ToString(), r.Text, StringComparison.Ordinal);
        Assert.Contains(b.Id.ToString(), r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.3", r.Text, StringComparison.Ordinal);
        Assert.Equal(AuditDecision.Invalid, rig.Audit.Single().Decision);

        Assert.False((await rig.CallAsync(ToolNames.RunCommand, ("host", "DB"), ("command", "uptime"))).IsError);
        Assert.False((await rig.CallAsync(ToolNames.RunCommand, ("host", b.Id.ToString()), ("command", "uptime"))).IsError);
        Assert.Equal([c.Id, b.Id], rig.Connector.Connects.Select(x => x.HostId));
    }

    [Fact]
    public async Task List_hosts_filters_and_never_returns_secrets()
    {
        var rig = new Rig();
        var jumpOff = rig.AddHost("hidden-bastion", AiPolicy.Off);
        var jump = rig.AddHost("bastion", AiPolicy.ReadOnly);
        var web = rig.Hosts.Add(new Host
        {
            Label = "web-1", Hostname = "web1.example", Port = 2222, Username = "deploy", AiPolicy = AiPolicy.Ask,
            Tags = ["prod", "Web"], JumpChain = [jumpOff.Id, jump.Id], IdentityId = Guid.NewGuid(), AiIdentityId = Guid.NewGuid(), Notes = "pw is hunter2",
        });
        var r = await rig.CallAsync(ToolNames.ListHosts);
        Assert.False(r.IsError);
        var hosts = r.Structured!.Value.GetProperty("hosts").EnumerateArray().ToList();
        Assert.Equal(["bastion", "web-1"], hosts.Select(h => h.GetProperty("label").GetString()));
        var w = hosts.Single(h => h.GetProperty("label").GetString() == "web-1");
        Assert.Equal(web.Id, w.GetProperty("id").GetGuid());
        Assert.Equal(("web1.example", 2222, "deploy", "Ask"), (w.GetProperty("hostname").GetString(), w.GetProperty("port").GetInt32(), w.GetProperty("username").GetString(), w.GetProperty("aiPolicy").GetString()));
        Assert.Equal(["(hidden jump host)", "bastion"], w.GetProperty("jumpChain").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("hunter2", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-bastion", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(web.IdentityId!.Value.ToString(), r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("identity", r.Text, StringComparison.OrdinalIgnoreCase);

        var byTag = await rig.CallAsync(ToolNames.ListHosts, ("tag", "web"));
        Assert.Single(byTag.Structured!.Value.GetProperty("hosts").EnumerateArray());
        var byQuery = await rig.CallAsync(ToolNames.ListHosts, ("query", "WEB1.EX"));
        Assert.Single(byQuery.Structured!.Value.GetProperty("hosts").EnumerateArray());
        var none = await rig.CallAsync(ToolNames.ListHosts, ("query", "zzz"));
        Assert.Empty(none.Structured!.Value.GetProperty("hosts").EnumerateArray());
        Assert.Contains("no host matches", none.Text, StringComparison.Ordinal);
        Assert.All(rig.Audit.Entries, e => Assert.Equal((AuditDecision.Allowed, (Guid?)null), (e.Decision, e.HostId)));
    }

    // ------------------------------------------------------------------ approval

    [Fact]
    public async Task Ask_host_requests_approval_with_summary_details_and_deadline()
    {
        var time = new ManualTime();
        var rig = new Rig(time: time);
        var host = rig.AddHost("web", AiPolicy.Ask);
        var r = await rig.CallAsync(ToolNames.RunCommand, T.Args(("host", "web"), ("command", "systemctl restart nginx"), ("cwd", "/srv"), ("timeout_s", 30)), T.Ai);
        Assert.False(r.IsError, r.Text);
        var req = Assert.Single(rig.Approval.Requests);
        Assert.Equal("systemctl restart nginx", req.Summary);
        Assert.Contains("Working directory: /srv", req.Details, StringComparison.Ordinal);
        Assert.Contains("Timeout: 30 s", req.Details, StringComparison.Ordinal);
        Assert.True(req.HighRisk);
        Assert.Contains("systemctl restart", req.Reason, StringComparison.Ordinal);
        Assert.Equal(T.Ai, req.Actor);
        Assert.Equal(host.Id, req.Host.Id);
        Assert.Equal(ToolNames.RunCommand, req.Tool.Name);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(120), req.Deadline);
        Assert.Equal(AuditDecision.Approved, rig.Audit.Single().Decision);
        Assert.Equal("ai:claude/opus", rig.Audit.Single().Actor);
        Assert.Equal(ConnectionOrigin.Ai, rig.Connector.Connects.Single().Origin);
        var (cmd, opts) = rig.Connector.Commands.Single();
        Assert.Equal(("systemctl restart nginx", "/srv", TimeSpan.FromSeconds(30), 64 * 1024), (cmd, opts.Cwd, opts.Timeout, opts.MaxOutputBytes));
    }

    [Fact]
    public async Task Write_approval_summaries_describe_the_change()
    {
        var rig = new Rig();
        var host = rig.AddHost("web", AiPolicy.Ask);
        rig.Connector.SftpFor(host.Id).Put("/etc/app.conf", "old");
        await rig.CallAsync(ToolNames.SftpWrite, ("host", "web"), ("path", "/etc/app.conf"), ("content", new string('x', 2150) + "\nline2"), ("mode", "overwrite"));
        await rig.CallAsync(ToolNames.SftpDelete, ("host", "web"), ("path", "/tmp/build"), ("recursive", true));
        await rig.CallAsync(ToolNames.SftpMkdir, ("host", "web"), ("path", "/tmp/new"));
        var reqs = rig.Approval.Requests.ToList();
        Assert.Equal("write /etc/app.conf (overwrite, 2.1 KB, 2 lines)", reqs[0].Summary);
        Assert.StartsWith("xxxx", reqs[0].Details, StringComparison.Ordinal);
        Assert.Equal("delete /tmp/build (recursive: everything inside it)", reqs[1].Summary);
        Assert.Equal("mkdir /tmp/new", reqs[2].Summary);
    }

    [Theory]
    [InlineData(ApprovalOutcome.Denied, AuditDecision.Denied, "The user denied")]
    [InlineData(ApprovalOutcome.TimedOut, AuditDecision.TimedOut, "Nobody approved")]
    [InlineData(ApprovalOutcome.Cancelled, AuditDecision.Cancelled, "cancelled")]
    public async Task Rejected_approvals_do_not_execute(ApprovalOutcome outcome, AuditDecision decision, string text)
    {
        var rig = new Rig();
        rig.AddHost("web", AiPolicy.Ask);
        rig.Approval.Outcome = outcome;
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "uptime"));
        Assert.True(r.IsError);
        Assert.Contains(text, r.Text, StringComparison.Ordinal);
        Assert.Empty(rig.Connector.Connects);
        Assert.Equal(decision, rig.Audit.Single().Decision);
    }

    [Fact]
    public async Task Approval_timeout_is_enforced_with_the_time_provider()
    {
        var time = new ManualTime();
        var rig = new Rig(time: time);
        rig.AddHost("web", AiPolicy.Ask);
        rig.Approval.Delay = TimeSpan.FromHours(1); // honours its token: returns Cancelled when the deadline fires
        var call = rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "uptime"));
        await rig.Approval.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), T.Ct);
        time.Advance(TimeSpan.FromSeconds(119));
        await Task.Delay(50, T.Ct);
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        var r = await call.WaitAsync(TimeSpan.FromSeconds(5), T.Ct);
        Assert.True(r.IsError);
        Assert.Contains("within 120 seconds", r.Text, StringComparison.Ordinal);
        var a = rig.Audit.Single();
        Assert.Equal(AuditDecision.TimedOut, a.Decision);
        Assert.Equal(121_000, a.DurationMs);
        Assert.Empty(rig.Connector.Connects);
    }

    [Fact]
    public async Task Approval_timeout_holds_even_if_the_ui_ignores_its_token()
    {
        var rig = new Rig(new ToolOptions { ApprovalTimeout = TimeSpan.FromMilliseconds(150) });
        rig.AddHost("web", AiPolicy.Ask);
        rig.Approval.Hang = true;
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "uptime")).WaitAsync(TimeSpan.FromSeconds(10), T.Ct);
        Assert.True(r.IsError);
        Assert.Equal(AuditDecision.TimedOut, rig.Audit.Single().Decision);
    }

    [Fact]
    public async Task Caller_cancellation_while_waiting_for_approval_throws_and_is_audited()
    {
        var rig = new Rig();
        rig.AddHost("web", AiPolicy.Ask);
        rig.Approval.Delay = TimeSpan.FromHours(1);
        using var cts = new CancellationTokenSource();
        var call = rig.CallAsync(ToolNames.RunCommand, T.Args(("host", "web"), ("command", "uptime")), ct: cts.Token);
        await rig.Approval.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), T.Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(AuditDecision.Cancelled, rig.Audit.Single().Decision);
        Assert.Empty(rig.Connector.Connects);
    }

    [Fact]
    public async Task Caller_cancellation_during_execution_throws_and_is_audited()
    {
        var rig = new Rig();
        rig.AddHost("web", AiPolicy.Trusted);
        var started = new TaskCompletionSource();
        rig.Connector.Exec = async (_, _, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return new ExecResult(null, "", "", false, false, false, Cancelled: true);
        };
        using var cts = new CancellationTokenSource();
        var call = rig.CallAsync(ToolNames.RunCommand, T.Args(("host", "web"), ("command", "sleep 100")), ct: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), T.Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        var a = rig.Audit.Single();
        Assert.Equal(AuditDecision.Cancelled, a.Decision);
        Assert.Equal(0, rig.Connector.OpenConnections); // lease disposed
    }

    [Fact]
    public async Task Already_cancelled_token_is_audited_once()
    {
        var rig = new Rig();
        rig.AddHost("web", AiPolicy.Trusted);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.CallAsync(ToolNames.RunCommand, T.Args(("host", "web"), ("command", "uptime")), ct: cts.Token));
        Assert.Equal(AuditDecision.Cancelled, rig.Audit.Single().Decision);
    }

    // ------------------------------------------------------------------ validation & audit

    [Fact]
    public async Task Validation_errors_are_results_and_audited_as_invalid()
    {
        var rig = new Rig();
        rig.AddHost("web", AiPolicy.Trusted);
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "uptime"), ("timeout_s", 601));
        Assert.True(r.IsError);
        Assert.Equal("Invalid arguments for run_command: Argument 'timeout_s' must be <= 600, got 601.", r.Text);
        r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"));
        Assert.StartsWith("Invalid arguments for run_command: Missing required argument 'command'", r.Text, StringComparison.Ordinal);
        r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "uptime"), ("sudo", true));
        Assert.Contains("Unknown argument 'sudo'", r.Text, StringComparison.Ordinal);
        r = await rig.CallAsync("format_disk", ("host", "web"));
        Assert.StartsWith("Unknown tool 'format_disk'. Available tools: list_hosts, run_command", r.Text, StringComparison.Ordinal);
        Assert.Equal(4, rig.Audit.Entries.Count);
        Assert.All(rig.Audit.Entries, e => Assert.Equal(AuditDecision.Invalid, e.Decision));
        Assert.Equal("format_disk", rig.Audit.Entries.Last().Tool);
        Assert.Empty(rig.Connector.Connects);
    }

    [Fact]
    public async Task User_origin_is_refused()
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.CallAsync(ToolNames.ListHosts, T.Args(), new ToolActor(ConnectionOrigin.User, "user", "User")));
    }

    [Fact]
    public async Task Audit_redacts_file_content_and_terminal_text()
    {
        var rig = new Rig();
        var host = rig.AddHost("web", AiPolicy.Trusted);
        rig.AddSession(host);
        await rig.CallAsync(ToolNames.SftpWrite, ("host", "web"), ("path", "a.txt"), ("content", "SECRET-CONTENT-123"), ("mode", "create"));
        await rig.CallAsync(ToolNames.SendToTerminal, ("session", "s1"), ("text", "export TOKEN=SECRET-TEXT-456"));
        await rig.CallAsync(ToolNames.SftpWrite, ("host", "web"), ("path", "b.txt"), ("content", 42), ("mode", "create")); // invalid, still redacted shape
        var entries = rig.Audit.Entries.ToList();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.DoesNotContain("SECRET", e.ArgumentsJson, StringComparison.Ordinal));
        using (var a0 = JsonDocument.Parse(entries[0].ArgumentsJson))
        {
            Assert.Equal("<18 chars>", a0.RootElement.GetProperty("content").GetString());
            Assert.Equal("a.txt", a0.RootElement.GetProperty("path").GetString());
            Assert.Equal("create", a0.RootElement.GetProperty("mode").GetString());
        }
        using (var a1 = JsonDocument.Parse(entries[1].ArgumentsJson))
            Assert.Equal("<28 chars>", a1.RootElement.GetProperty("text").GetString());
        Assert.Equal(host.Id, entries[0].HostId);
        Assert.Equal(AuditDecision.Invalid, entries[2].Decision);
        Assert.Equal("{}", ToolInvoker.RedactArguments(default));
        Assert.Equal("""{"<invalid>":"Array"}""", ToolInvoker.RedactArguments(T.Json("[1]")));
        Assert.Equal("""{"x":"<object>","n":1}""", ToolInvoker.RedactArguments(T.Json("""{"x":{"secret":1},"n":1}""")));
    }

    [Fact]
    public async Task Exactly_one_audit_entry_per_call_under_concurrency()
    {
        var rig = new Rig();
        rig.AddHost("ro", AiPolicy.ReadOnly);
        rig.AddHost("ask", AiPolicy.Ask);
        rig.AddHost("trusted", AiPolicy.Trusted);
        rig.Approval.Delay = TimeSpan.FromMilliseconds(5);
        rig.Connector.Exec = async (cmd, _, ct) => { await Task.Delay(Random.Shared.Next(1, 10), ct); return new ExecResult(0, cmd, "", false, false, false, false); };
        var calls = Enumerable.Range(0, 120).Select(i => rig.CallAsync(ToolNames.RunCommand,
            ("host", (i % 3) switch { 0 => "ro", 1 => "ask", _ => "trusted" }), ("command", i % 2 == 0 ? "uptime" : "apt-get update"))).ToList();
        var results = await Task.WhenAll(calls);
        Assert.Equal(120, rig.Audit.Entries.Count);
        Assert.Equal(20, results.Count(r => r.IsError)); // ro + apt-get update
        Assert.Equal(20, rig.Audit.Entries.Count(e => e.Decision == AuditDecision.PolicyRejected));
        Assert.Equal(40, rig.Audit.Entries.Count(e => e.Decision == AuditDecision.Approved));
        Assert.Equal(60, rig.Audit.Entries.Count(e => e.Decision == AuditDecision.Allowed));
        Assert.Equal(120, rig.Audit.Entries.Select(e => e.Id).Distinct().Count());
        Assert.Equal(0, rig.Connector.OpenConnections);
    }

    [Fact]
    public async Task Audit_store_failure_does_not_break_the_call()
    {
        var hosts = new MemHosts();
        hosts.Add(new Host { Label = "web", Hostname = "h", AiPolicy = AiPolicy.Trusted });
        var layer = ToolServices.Create(hosts, new FakeConnector(hosts), new FakeApproval(), new ThrowingAudit());
        var r = await layer.Invoker.InvokeAsync(new ToolCallRequest(ToolNames.RunCommand, T.Args(("host", "web"), ("command", "uptime"))), T.Mcp, T.Ct);
        Assert.False(r.IsError);
    }

    private sealed class ThrowingAudit : IAuditLogStore
    {
        public Task AppendAsync(AuditEntry entry, CancellationToken ct = default) => throw new IOException("disk full");
        public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------ run_command results

    [Fact]
    public async Task Run_command_result_reports_exit_code_streams_and_truncation()
    {
        var rig = new Rig(new ToolOptions { MaxCommandOutputBytes = 1000 });
        rig.AddHost("web", AiPolicy.Trusted);
        rig.Connector.Exec = (_, o, _) => Task.FromResult(new ExecResult(null, new string('a', o.MaxOutputBytes), "warn\n", true, false, false, false) { ExitSignal = "PIPE" });
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "cat big"));
        Assert.False(r.IsError);
        Assert.StartsWith("exit_code: none\nsignal: PIPE\ntruncated: stdout reached the 1000-byte cap", r.Text, StringComparison.Ordinal);
        Assert.Contains("--- stderr ---\nwarn\n", r.Text, StringComparison.Ordinal);
        var s = r.Structured!.Value;
        Assert.True(s.GetProperty("stdoutTruncated").GetBoolean());
        Assert.False(s.GetProperty("stderrTruncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("exitCode").ValueKind);
        Assert.Equal(1000, rig.Connector.Commands.Single().Options.MaxOutputBytes);

        rig.Connector.Exec = (_, _, _) => Task.FromResult(new ExecResult(3, "out", "err", false, false, false, false));
        r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "false"));
        Assert.False(r.IsError);
        Assert.Equal("exit_code: 3\n--- stdout ---\nout\n--- stderr ---\nerr", r.Text);
        Assert.Equal(3, rig.Audit.Entries.Last().ExitCode);

        rig.Connector.Exec = (_, _, _) => Task.FromResult(new ExecResult(null, "partial", "", false, false, TimedOut: true, false));
        r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "sleep 99"), ("timeout_s", 5));
        Assert.Contains("timed_out: true (killed after 5 s)", r.Text, StringComparison.Ordinal);
        Assert.True(r.Structured!.Value.GetProperty("timedOut").GetBoolean());
        Assert.Equal("Timed out after 5 s.", rig.Audit.Entries.Last().Error);
    }

    [Theory]
    [InlineData(SshFailureKind.HostKeyUnknown, "the host key of 'web' is not trusted yet; connect once from MySSH and accept the key")]
    [InlineData(SshFailureKind.HostKeyChanged, "has CHANGED")]
    [InlineData(SshFailureKind.AuthenticationFailed, "authentication to 'web' failed")]
    [InlineData(SshFailureKind.PassphraseRequired, "passphrase is not saved")]
    [InlineData(SshFailureKind.Network, "could not be reached")]
    [InlineData(SshFailureKind.Timeout, "timed out")]
    public async Task Connection_failures_are_friendly_and_name_the_kind(SshFailureKind kind, string text)
    {
        var rig = new Rig();
        var host = rig.AddHost("web", AiPolicy.Trusted);
        rig.Connector.ConnectError = new SshConnectException(kind, "raw message", 0, "web");
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "uptime"));
        Assert.True(r.IsError);
        Assert.Contains($"[{kind}]", r.Text, StringComparison.Ordinal);
        Assert.Contains(text, r.Text, StringComparison.Ordinal);
        var a = rig.Audit.Single();
        Assert.Equal(AuditDecision.Allowed, a.Decision);
        Assert.Equal(host.Id, a.HostId);
        Assert.StartsWith(kind.ToString(), a.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jump_host_failures_name_the_hop()
    {
        var rig = new Rig();
        rig.AddHost("web", AiPolicy.Trusted);
        rig.Connector.ConnectError = new SshConnectException(SshFailureKind.HostKeyUnknown, "x", 0, "bastion");
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "uptime"));
        Assert.Contains("jump host 'bastion'", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_execution_failures_become_error_results()
    {
        var rig = new Rig();
        rig.AddHost("web", AiPolicy.Trusted);
        rig.Connector.Exec = (_, _, _) => throw new SshConnectionLostException("boom");
        var r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "uptime"));
        Assert.True(r.IsError);
        Assert.Contains("was lost", r.Text, StringComparison.Ordinal);
        rig.Connector.Exec = (_, _, _) => throw new VaultLockedException();
        r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "uptime"));
        Assert.Contains("vault is locked", r.Text, StringComparison.Ordinal);
        rig.Connector.Exec = (_, _, _) => throw new InvalidOperationException("weird");
        r = await rig.CallAsync(ToolNames.RunCommand, ("host", "web"), ("command", "uptime"));
        Assert.True(r.IsError);
        Assert.Contains("Internal error", r.Text, StringComparison.Ordinal);
        Assert.Equal(3, rig.Audit.Entries.Count);
        Assert.All(rig.Audit.Entries, e => Assert.NotNull(e.Error));
        Assert.Equal(0, rig.Connector.OpenConnections);
    }

    // ------------------------------------------------------------------ SFTP

    [Fact]
    public async Task Sftp_read_returns_text_truncates_and_refuses_binary()
    {
        var rig = new Rig();
        var host = rig.AddHost("web", AiPolicy.ReadOnly);
        var fs = rig.Connector.SftpFor(host.Id);
        fs.Put("/etc/motd", "hello 世界\n");
        fs.Put("/home/u/big.txt", string.Concat(Enumerable.Repeat("é", 100))); // 200 bytes
        fs.Put("/bin/ls", [0x7f, 0x45, 0x4c, 0x46, 0, 1, 2]);
        fs.Put("/tmp/latin1", [0x68, 0xe9, 0x6c, 0x6c, 0x6f]);
        fs.Link("/home/u/motd-link", "/etc/motd");
        Assert.Equal("/home/u/x", ToolInvoker.RemotePath("~/x", "/home/u"));
        Assert.Equal("/home/u", ToolInvoker.RemotePath("~", "/home/u"));
        Assert.Equal("/etc", ToolInvoker.RemotePath("/var/../etc/", "/home/u"));
        Assert.Equal("/home/u/~x", ToolInvoker.RemotePath("~x", "/home/u"));

        var r = await rig.CallAsync(ToolNames.SftpRead, ("host", "web"), ("path", "/etc/motd"));
        Assert.False(r.IsError, r.Text);
        Assert.Equal("hello 世界\n", r.Text);
        Assert.False(r.Structured!.Value.GetProperty("truncated").GetBoolean());

        r = await rig.CallAsync(ToolNames.SftpRead, ("host", "web"), ("path", "big.txt"), ("max_bytes", 51)); // cut inside a 2-byte char
        Assert.False(r.IsError, r.Text);
        Assert.StartsWith(new string('é', 25) + "\n[truncated: showing the first 51 bytes of 200", r.Text, StringComparison.Ordinal);
        Assert.True(r.Structured!.Value.GetProperty("truncated").GetBoolean());
        Assert.Equal(new string('é', 25), r.Structured!.Value.GetProperty("content").GetString());

        r = await rig.CallAsync(ToolNames.SftpRead, ("host", "web"), ("path", "/bin/ls"));
        Assert.True(r.IsError);
        Assert.Contains("binary", r.Text, StringComparison.Ordinal);
        Assert.Contains("xxd", r.Text, StringComparison.Ordinal);
        r = await rig.CallAsync(ToolNames.SftpRead, ("host", "web"), ("path", "/tmp/latin1"));
        Assert.True(r.IsError);
        Assert.Contains("not valid UTF-8", r.Text, StringComparison.Ordinal);

        r = await rig.CallAsync(ToolNames.SftpRead, ("host", "web"), ("path", "motd-link"));
        Assert.Equal("hello 世界\n", r.Text);
        r = await rig.CallAsync(ToolNames.SftpRead, ("host", "web"), ("path", "/etc"));
        Assert.Contains("is a directory; use sftp_list", r.Text, StringComparison.Ordinal);
        r = await rig.CallAsync(ToolNames.SftpRead, ("host", "web"), ("path", "/nope"));
        Assert.Contains("No such file: /nope", r.Text, StringComparison.Ordinal);
        r = await rig.CallAsync(ToolNames.SftpRead, ("host", "web"), ("path", "/etc/motd"), ("max_bytes", 1024 * 1024 + 1));
        Assert.Contains("must be <= 1048576", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sftp_list_and_stat()
    {
        var rig = new Rig(new ToolOptions { MaxSftpListEntries = 2 });
        var host = rig.AddHost("web", AiPolicy.ReadOnly);
        var fs = rig.Connector.SftpFor(host.Id);
        fs.Put("/srv/a.txt", "a");
        fs.Mkdir("/srv/b");
        fs.Link("/srv/c", "/srv/a.txt");
        var r = await rig.CallAsync(ToolNames.SftpList, ("host", "web"), ("path", "/srv"));
        Assert.False(r.IsError, r.Text);
        Assert.StartsWith("/srv: 3 entries (showing the first 2)\n", r.Text, StringComparison.Ordinal);
        Assert.Contains(" b/\n", r.Text, StringComparison.Ordinal);
        var s = r.Structured!.Value;
        Assert.True(s.GetProperty("truncated").GetBoolean());
        Assert.Equal(3, s.GetProperty("totalEntries").GetInt32());
        Assert.Equal(["file", "directory"], s.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("kind").GetString()));

        r = await rig.CallAsync(ToolNames.SftpStat, ("host", "web"), ("path", "/srv/c"));
        Assert.False(r.IsError);
        Assert.True(r.Structured!.Value.GetProperty("exists").GetBoolean());
        Assert.Equal("symlink", r.Structured!.Value.GetProperty("entry").GetProperty("kind").GetString());
        Assert.Equal("/srv/a.txt", r.Structured!.Value.GetProperty("entry").GetProperty("linkTarget").GetString());
        r = await rig.CallAsync(ToolNames.SftpStat, ("host", "web"), ("path", "/srv/none"));
        Assert.False(r.IsError);
        Assert.False(r.Structured!.Value.GetProperty("exists").GetBoolean());
        r = await rig.CallAsync(ToolNames.SftpList, ("host", "web"), ("path", "/missing"));
        Assert.True(r.IsError);
        Assert.Contains("No such file or directory", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sftp_write_create_overwrite_mkdir_delete()
    {
        var rig = new Rig();
        var host = rig.AddHost("web", AiPolicy.Trusted);
        var fs = rig.Connector.SftpFor(host.Id);
        fs.Put("/srv/exists.txt", "old");

        var r = await rig.CallAsync(ToolNames.SftpWrite, ("host", "web"), ("path", "/srv/new.txt"), ("content", "héllo"), ("mode", "create"));
        Assert.False(r.IsError, r.Text);
        Assert.Equal("héllo"u8.ToArray(), fs.Read("/srv/new.txt"));
        Assert.Equal(("created", 6), (r.Structured!.Value.GetProperty("mode").GetString(), r.Structured!.Value.GetProperty("bytes").GetInt32()));
        Assert.Empty(rig.Approval.Requests);

        r = await rig.CallAsync(ToolNames.SftpWrite, ("host", "web"), ("path", "/srv/exists.txt"), ("content", "x"), ("mode", "create"));
        Assert.True(r.IsError);
        Assert.Contains("already exists; use mode \"overwrite\"", r.Text, StringComparison.Ordinal);
        Assert.Equal("old"u8.ToArray(), fs.Read("/srv/exists.txt"));

        r = await rig.CallAsync(ToolNames.SftpWrite, ("host", "web"), ("path", "/srv/exists.txt"), ("content", "new"), ("mode", "overwrite"));
        Assert.False(r.IsError, r.Text);
        Assert.Single(rig.Approval.Requests); // overwrite always asks
        Assert.Equal("new"u8.ToArray(), fs.Read("/srv/exists.txt"));

        r = await rig.CallAsync(ToolNames.SftpWrite, ("host", "web"), ("path", "/nodir/x.txt"), ("content", "x"), ("mode", "create"));
        Assert.True(r.IsError);

        r = await rig.CallAsync(ToolNames.SftpMkdir, ("host", "web"), ("path", "/srv/d"));
        Assert.False(r.IsError, r.Text);
        Assert.True(fs.Exists("/srv/d"));
        r = await rig.CallAsync(ToolNames.SftpMkdir, ("host", "web"), ("path", "/srv/d"));
        Assert.Contains("already exists", r.Text, StringComparison.Ordinal);

        fs.Put("/srv/d/inner", "i");
        r = await rig.CallAsync(ToolNames.SftpDelete, ("host", "web"), ("path", "/srv/d"));
        Assert.True(r.IsError);
        Assert.Contains("recursive=true", r.Text, StringComparison.Ordinal);
        r = await rig.CallAsync(ToolNames.SftpDelete, ("host", "web"), ("path", "/srv/d"), ("recursive", true));
        Assert.False(r.IsError, r.Text);
        Assert.False(fs.Exists("/srv/d/inner"));
        r = await rig.CallAsync(ToolNames.SftpDelete, ("host", "web"), ("path", "/srv/none"));
        Assert.Contains("No such file or directory", r.Text, StringComparison.Ordinal);
        r = await rig.CallAsync(ToolNames.SftpDelete, ("host", "web"), ("path", "/"), ("recursive", true));
        Assert.Contains("Refusing to delete /", r.Text, StringComparison.Ordinal);
        r = await rig.CallAsync(ToolNames.SftpDelete, ("host", "web"), ("path", "~"), ("recursive", true));
        Assert.Contains("Refusing to delete /home/u", r.Text, StringComparison.Ordinal);
        Assert.True(fs.Exists("/home/u"));
        Assert.Equal(6, rig.Approval.Requests.Count); // 1 overwrite + 5 deletes (approval comes before the existence check)
    }

    [Fact]
    public async Task Sftp_write_size_limit_is_checked_in_encoded_bytes()
    {
        var rig = new Rig(new ToolOptions { MaxSftpWriteBytes = 10 });
        rig.AddHost("web", AiPolicy.Trusted);
        var r = await rig.CallAsync(ToolNames.SftpWrite, ("host", "web"), ("path", "a"), ("content", "ééééé" + "é"), ("mode", "create")); // 6 chars, 12 bytes
        Assert.True(r.IsError);
        Assert.Contains("content is 12 bytes, the maximum is 10", r.Text, StringComparison.Ordinal);
        Assert.Equal(AuditDecision.Invalid, rig.Audit.Single().Decision);
        Assert.Empty(rig.Connector.Connects);
    }

    // ------------------------------------------------------------------ terminal

    [Fact]
    public async Task Send_to_terminal_rejects_control_characters()
    {
        var rig = new Rig();
        var host = rig.AddHost("web", AiPolicy.Trusted);
        rig.AddSession(host);
        foreach (var bad in new[] { "\u001b[2J", "\u0003", "ls\u0004", "\u007f", "\u009b31m" })
        {
            var r = await rig.CallAsync(ToolNames.SendToTerminal, ("session", "s1"), ("text", bad));
            Assert.True(r.IsError, bad);
            Assert.Contains("control character U+", r.Text, StringComparison.Ordinal);
        }
        Assert.Empty(rig.Terminals.Sent);
        Assert.All(rig.Audit.Entries, e => Assert.Equal(AuditDecision.Invalid, e.Decision));
        var ok = await rig.CallAsync(ToolNames.SendToTerminal, ("session", "s1"), ("text", "printf 'a\\tb'\t# tab ok"));
        Assert.False(ok.IsError, ok.Text);
        Assert.Contains("Enter was NOT pressed", ok.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_to_terminal_to_disconnected_session_fails()
    {
        var rig = new Rig();
        var host = rig.AddHost("web", AiPolicy.Trusted);
        rig.AddSession(host, connected: false);
        var r = await rig.CallAsync(ToolNames.SendToTerminal, ("session", "s1"), ("text", "ls"));
        Assert.True(r.IsError);
        Assert.Contains("not connected", r.Text, StringComparison.Ordinal);
        Assert.Equal(AuditDecision.Allowed, rig.Audit.Single().Decision);
    }

    [Fact]
    public async Task Read_terminal_without_registry_has_no_sessions()
    {
        var hosts = new MemHosts();
        hosts.Add(new Host { Label = "web", Hostname = "h", AiPolicy = AiPolicy.Trusted });
        var layer = ToolServices.Create(hosts, new FakeConnector(hosts), new FakeApproval(), new MemAudit());
        var r = await layer.Invoker.InvokeAsync(new ToolCallRequest(ToolNames.ListSessions, default), T.Mcp, T.Ct);
        Assert.False(r.IsError);
        Assert.Contains("No terminal sessions", r.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    [Fact]
    public void Decode_text_handles_bom_split_sequences_and_other_encodings()
    {
        Assert.Equal("abc", ToolInvoker.DecodeText([0xEF, 0xBB, 0xBF, (byte)'a', (byte)'b', (byte)'c'], false, new UTF8Encoding(false)));
        Assert.Equal("a", ToolInvoker.DecodeText([(byte)'a', 0xE4, 0xB8], true, new UTF8Encoding(false)));
        Assert.Null(ToolInvoker.DecodeText([(byte)'a', 0xE4, 0xB8], false, new UTF8Encoding(false)));
        Assert.Null(ToolInvoker.DecodeText([(byte)'a', 0, (byte)'b'], false, Encoding.Latin1));
        Assert.Equal("hé", ToolInvoker.DecodeText([(byte)'h', 0xE9], false, Encoding.Latin1));
        Assert.Equal(0, ToolInvoker.CountLines(""));
        Assert.Equal(1, ToolInvoker.CountLines("a\n"));
        Assert.Equal(2, ToolInvoker.CountLines("a\r\nb"));
        Assert.Equal(3, ToolInvoker.CountLines("a\nb\rc"));
    }
}
