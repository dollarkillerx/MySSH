using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using ModelContextProtocol.Client;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.Tools;
using MySSH.UI.Carbon;

namespace MySSH.Desktop.Tests;

/// <summary>P3 desktop pieces: approval modal, MCP controller (real server + SDK client), AI fields of the host editor.</summary>
[Collection("Loc")]
public sealed class McpDesktopTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly Host Web = new() { Label = "web-01", Hostname = "10.0.0.5", AiPolicy = AiPolicy.Ask };

    static ApprovalRequest Request(bool highRisk, TimeSpan deadline) => new(
        Guid.NewGuid(), new ToolActor(ConnectionOrigin.Mcp, "mcp:claude-code", "claude-code (MCP)"),
        new ToolCatalog().Find("run_command")!, Web,
        highRisk ? "sudo systemctl restart nginx" : "df -h", null,
        highRisk ? "High-risk rule matched: systemctl restart" : "The host policy asks before running commands.", highRisk,
        DateTimeOffset.UtcNow + deadline);

    [AvaloniaFact]
    public async Task Approval_modal_allows_denies_and_closes_on_cancel_or_deadline()
    {
        Loc.Instance.SetCulture("en-US");
        var w = new MainWindow { Width = 1280, Height = 800 };
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        w.Show();
        var modal = w.FindControl<Modal>("ApprovalModal")!;

        var pending = w.RequestAsync(Request(highRisk: true, TimeSpan.FromSeconds(120)), Ct);
        MainWindowUiTestsHelpers.Pump();
        Assert.True(modal.IsOpen);
        Assert.True(modal.IsDanger);
        Assert.True(w.FindControl<InlineNotification>("ApprovalRisk")!.IsVisible);
        Assert.Contains("systemctl restart", w.FindControl<SelectableTextBlock>("ApprovalSummary")!.Text, StringComparison.Ordinal);
        MainWindowUiTestsHelpers.Save(w, "20-approval-high-risk-light");
        modal.Close(ModalResult.Primary);
        Assert.Equal(ApprovalOutcome.Approved, await pending);

        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Loc.Instance.SetCulture("zh-CN");
        pending = w.RequestAsync(Request(highRisk: false, TimeSpan.FromSeconds(120)), Ct);
        MainWindowUiTestsHelpers.Pump();
        Assert.False(modal.IsDanger);
        MainWindowUiTestsHelpers.Save(w, "21-approval-ask-dark-zh");
        Loc.Instance.SetCulture("en-US");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        modal.Close(ModalResult.Secondary);
        Assert.Equal(ApprovalOutcome.Denied, await pending);

        // the caller (MCP client) gives up: the modal closes and reports Cancelled
        using (var cts = new CancellationTokenSource())
        {
            pending = w.RequestAsync(Request(false, TimeSpan.FromSeconds(120)), cts.Token);
            MainWindowUiTestsHelpers.Pump();
            Assert.True(modal.IsOpen);
            await cts.CancelAsync();
            MainWindowUiTestsHelpers.Pump();
            Assert.Equal(ApprovalOutcome.Cancelled, await pending);
            Assert.False(modal.IsOpen);
        }

        // nobody answers: closes itself at the deadline → TimedOut
        pending = w.RequestAsync(Request(false, TimeSpan.FromSeconds(1)), Ct);
        for (var i = 0; i < 40 && !pending.IsCompleted; i++) { await Task.Delay(100, Ct); MainWindowUiTestsHelpers.Pump(); }
        Assert.Equal(ApprovalOutcome.TimedOut, await pending);
        w.Close();
    }

    [Fact]
    public async Task Controller_starts_the_real_server_persists_port_and_token_and_serves_tools()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var s = t.Storage;
        await s.Hosts.SaveAsync(Web, Ct);
        await s.Hosts.SaveAsync(new Host { Label = "hidden", Hostname = "10.0.0.9" }, Ct); // AiPolicy Off
        var tools = ToolServices.Create(s.Hosts, new ThrowingConnector(), RejectingPrompts.Instance, s.AuditLog);
        var sockDir = Directory.CreateTempSubdirectory("mcp-");
        try
        {
            await using var mcp = new McpController(s.Settings, s.Secrets, tools.Catalog, tools.Invoker,
                o => o with { LocalSocketPath = Path.Combine(sockDir.FullName, "m.sock") });
            await mcp.InitializeAsync(Ct);
            Assert.False(mcp.IsEnabled); // off by default
            Assert.False(mcp.IsRunning);

            await mcp.SetEnabledAsync(true, Ct);
            Assert.True(mcp.IsRunning, mcp.StartError);
            Assert.NotNull(mcp.Token);
            Assert.Equal(mcp.Token, await s.Secrets.GetAsync(McpPreferences.TokenKey, Ct)); // vault-encrypted
            var port = mcp.HttpEndpoint!.Port;
            Assert.Contains($"127.0.0.1:{port}/mcp", mcp.ClaudeCodeCommand, StringComparison.Ordinal);
            Assert.Contains("Authorization: Bearer " + mcp.Token, mcp.ClaudeCodeCommand, StringComparison.Ordinal);

            // a real MCP client over HTTP sees the tools and only the hosts whose policy is not Off
            await using (var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = mcp.HttpEndpoint, TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + mcp.Token },
            }), cancellationToken: Ct))
            {
                var listed = await client.ListToolsAsync(cancellationToken: Ct);
                Assert.Contains(listed, x => x.Name == "run_command");
                var hosts = await client.CallToolAsync("list_hosts", new Dictionary<string, object?>(), cancellationToken: Ct);
                var text = JsonSerializer.Serialize(hosts.Content);
                Assert.Contains("web-01", text, StringComparison.Ordinal);
                Assert.DoesNotContain("hidden", text, StringComparison.Ordinal);
            }
            var audit = await s.AuditLog.QueryAsync(new AuditQuery { ActorPrefix = "mcp:" }, Ct);
            Assert.Contains(audit, e => e.Tool == "list_hosts" && e.Decision == AuditDecision.Allowed);

            // restart keeps port and token; regenerating rotates the token live
            await mcp.SetEnabledAsync(false, Ct);
            Assert.False(mcp.IsRunning);
            await mcp.SetEnabledAsync(true, Ct);
            Assert.Equal(port, mcp.HttpEndpoint!.Port);
            var old = mcp.Token;
            await mcp.RegenerateTokenAsync(Ct);
            Assert.NotEqual(old, mcp.Token);
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, mcp.HttpEndpoint) { Content = new StringContent("{}") };
            req.Headers.Authorization = new("Bearer", old);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.SendAsync(req, Ct)).StatusCode);
        }
        finally { sockDir.Delete(true); }
    }

    [Fact]
    public async Task Host_editor_saves_ai_identity_and_allowlist()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var s = t.Storage;
        var ai = new Identity { Label = "ai-reader", Username = "reader", Kind = IdentityKind.Password };
        await s.Identities.SaveAsync(ai, new IdentitySecrets("pw", null, null), Ct);
        var ed = new HostEditorViewModel(s.Hosts, s.Identities, s.Proxies, null);
        await ed.LoadAsync(Ct);
        Assert.False(ed.IsAiEnabled);
        ed.Label = "db"; ed.Hostname = "10.0.0.7";
        ed.AiPolicy = AiPolicy.ReadOnly;
        Assert.True(ed.IsAiEnabled && ed.IsAiReadOnly);
        ed.SelectedAiIdentity = ed.AiIdentityOptions.Single(o => o.Id == ai.Id);
        ed.AiAllowlist = "df -h\n  systemctl status nginx  \n\ndf -h\n";
        var host = await ed.SaveAsync(Ct);
        Assert.NotNull(host);

        var saved = (await s.Hosts.GetAsync(host!.Id, Ct))!;
        Assert.Equal(ai.Id, saved.AiIdentityId);
        Assert.Equal(["df -h", "systemctl status nginx"], saved.AiCommandAllowlist);

        var again = new HostEditorViewModel(s.Hosts, s.Identities, s.Proxies, saved);
        await again.LoadAsync(Ct);
        Assert.Equal(ai.Id, again.SelectedAiIdentity!.Id);
        Assert.Equal("df -h\nsystemctl status nginx", again.AiAllowlist);
        again.SelectedAiIdentity = again.AiIdentityOptions[0]; // back to "same as connection identity"
        Assert.Null((await again.SaveAsync(Ct))!.AiIdentityId);
    }

    sealed class ThrowingConnector : MySSH.Ssh.ISshConnector
    {
        public Task<MySSH.Ssh.ISshConnection> ConnectAsync(Guid hostId, ConnectionOrigin origin, CancellationToken ct = default) =>
            throw new MySSH.Ssh.SshConnectException(MySSH.Ssh.SshFailureKind.Network, "no network in tests");
    }
}

public sealed class SingleInstanceTests
{
    [Fact]
    public void Second_instance_is_refused_until_the_first_releases_the_lock()
    {
        var dir = Directory.CreateTempSubdirectory("myssh-lock-");
        try
        {
            var paths = new AppPaths { DataDirectory = Path.Combine(dir.FullName, "data"), LegacyDataDirectory = Path.Combine(dir.FullName, "legacy") };
            using (var first = SingleInstance.TryAcquire(paths))
            {
                Assert.NotNull(first);
                Assert.Null(SingleInstance.TryAcquire(paths));
            }
            using var again = SingleInstance.TryAcquire(paths);
            Assert.NotNull(again);
        }
        finally { dir.Delete(true); }
    }
}

public sealed class JumpHostEditorTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Add_hop_is_disabled_until_a_jump_host_is_picked()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var s = t.Storage;
        var ed = new HostEditorViewModel(s.Hosts, s.Identities, s.Proxies, null);
        await ed.LoadAsync(Ct);
        Assert.False(ed.HasJumpCandidates); // no other host yet: the picker explains it
        Assert.False(ed.AddJumpCommand.CanExecute(null));

        await s.Hosts.SaveAsync(new Host { Label = "bastion", Hostname = "10.0.0.1" }, Ct);
        ed = new HostEditorViewModel(s.Hosts, s.Identities, s.Proxies, null);
        await ed.LoadAsync(Ct);
        Assert.True(ed.HasJumpCandidates);
        Assert.False(ed.AddJumpCommand.CanExecute(null)); // nothing picked yet
        var canExecuteChanged = 0;
        ed.AddJumpCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;
        ed.JumpHostToAdd = ed.AvailableJumpHosts.Single();
        Assert.True(canExecuteChanged > 0);
        Assert.True(ed.AddJumpCommand.CanExecute(null));
        ed.AddJumpCommand.Execute(null);
        Assert.Equal("bastion", Assert.Single(ed.JumpChain).Label);
        Assert.False(ed.HasJumpCandidates);
        Assert.False(ed.AddJumpCommand.CanExecute(null));
    }
}

public sealed class WorkingDirectoryTests
{
    [Theory]
    [InlineData("file://web-01/var/log", "/var/log")]
    [InlineData("file://web-01/srv/my%20app/%E7%9B%AE%E5%BD%95", "/srv/my app/目录")]
    [InlineData("/home/deploy", "/home/deploy")]
    [InlineData("relative/path", null)]
    [InlineData("", null)]
    public void Shell_reported_directory_is_parsed(string reported, string? expected)
    {
        var t = new TerminalSessionViewModel(new NoConnector(), new FakeUi(), Guid.NewGuid(), "h", "—");
        t.NotifyWorkingDirectory(reported);
        Assert.Equal(expected, t.WorkingDirectory);
    }

    sealed class NoConnector : MySSH.Ssh.ISshConnector
    {
        public Task<MySSH.Ssh.ISshConnection> ConnectAsync(Guid hostId, ConnectionOrigin origin, CancellationToken ct = default) =>
            throw new InvalidOperationException();
    }
}
