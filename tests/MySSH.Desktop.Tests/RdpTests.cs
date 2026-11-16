using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Tests;

[Collection("Loc")]
public sealed class RdpTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Rdp_file_has_address_and_user_but_never_a_password()
    {
        var text = RdpFile.Build(new RdpTarget("127.0.0.1", 52011, @"CORP\alice", "win"));
        Assert.Contains("full address:s:127.0.0.1:52011\r\n", text, StringComparison.Ordinal);
        Assert.Contains(@"username:s:CORP\alice" + "\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("full address:s:[fe80::1]:3389", RdpFile.Build(new RdpTarget("fe80::1", 3389, null, "v6")), StringComparison.Ordinal);
        // control characters cannot inject extra settings
        Assert.DoesNotContain("\nusername:s:evil", RdpFile.Build(new RdpTarget("h", 3389, "bob\r\nusername:s:evil", "x")), StringComparison.Ordinal);
    }

    [Fact]
    public void Rdp_file_is_owner_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        var dir = Path.Combine(Directory.CreateTempSubdirectory("rdp-").FullName, "rdp");
        var path = RdpFile.Write(dir, "s-1234/../x", new RdpTarget("h", 3389, null, "t"));
        Assert.Equal(dir, Path.GetDirectoryName(path)); // the name cannot escape the directory
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
    }

    [Theory]
    [InlineData(AppPlatform.MacOS, "/Applications/Windows App.app", null, RdpClientKind.WindowsApp, "/usr/bin/open")]
    [InlineData(AppPlatform.Linux, null, "remmina", RdpClientKind.Remmina, "/usr/bin/remmina")]
    [InlineData(AppPlatform.Linux, null, "xfreerdp3", RdpClientKind.FreeRdp, "/usr/bin/xfreerdp3")]
    public void Client_detection_and_arguments(AppPlatform platform, string? existing, string? onPath, RdpClientKind kind, string command)
    {
        var started = new List<(string, IReadOnlyList<string>)>();
        var launcher = new RdpLauncher(platform, p => p == existing, n => n == onPath ? "/usr/bin/" + n : null,
            (c, a) => { started.Add((c, a)); return true; });
        var client = launcher.Launch("/data/rdp/s-1.rdp");
        Assert.NotNull(client);
        Assert.Equal(kind, client.Kind);
        var (cmd, args) = Assert.Single(started);
        Assert.Equal(command, cmd);
        Assert.Contains("/data/rdp/s-1.rdp", args);
        if (kind == RdpClientKind.WindowsApp) Assert.Equal(["-b", RdpLauncher.WindowsAppBundleId, "/data/rdp/s-1.rdp"], args);
    }

    [Theory]
    [InlineData(AppPlatform.MacOS)]
    [InlineData(AppPlatform.Linux)]
    public void Missing_client_is_reported_not_started(AppPlatform platform)
    {
        var started = 0;
        var launcher = new RdpLauncher(platform, _ => false, _ => null, (_, _) => { started++; return true; });
        Assert.Null(launcher.Detect());
        Assert.Null(launcher.Launch("/x.rdp"));
        Assert.Equal(0, started);
    }

    [Fact]
    public async Task Host_editor_rdp_rules()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var s = t.Storage;
        var bastion = new Host { Label = "bastion", Hostname = "10.0.0.1" };
        var bastion2 = new Host { Label = "bastion2", Hostname = "10.0.0.2" };
        var otherWin = new Host { Label = "other-win", Hostname = "10.0.0.8", Protocol = HostProtocol.Rdp, Port = 3389 };
        foreach (var h in new[] { bastion, bastion2, otherWin }) await s.Hosts.SaveAsync(h, Ct);

        var ed = new HostEditorViewModel(s.Hosts, s.Identities, s.Proxies, null);
        await ed.LoadAsync(Ct);
        Assert.DoesNotContain(ed.AvailableJumpHosts, o => o.Label == "other-win"); // RDP hosts are never jump hosts
        ed.ProtocolIsRdp = true;
        Assert.Equal("3389", ed.Port);
        Assert.True(ed.IsRdp);
        ed.Label = "win-01"; ed.Hostname = "10.0.0.9"; ed.Username = "Administrator";
        ed.AuthIsKey = true; ed.PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----";
        Assert.False(ed.Validate());
        Assert.NotNull(ed.AuthError); // RDP needs a password
        ed.AuthIsPassword = true; ed.Password = "Win-pw";
        ed.AiPolicy = AiPolicy.Trusted; // ignored for RDP
        ed.JumpHostToAdd = ed.AvailableJumpHosts.First(o => o.Label == "bastion");
        ed.AddJumpCommand.Execute(null);
        Assert.False(ed.HasJumpCandidates); // RDP: one forwarding SSH host
        ed.JumpHostToAdd = ed.AvailableJumpHosts.FirstOrDefault();
        Assert.False(ed.AddJumpCommand.CanExecute(null));

        var saved = (await ed.SaveAsync(Ct))!;
        Assert.Equal((HostProtocol.Rdp, 3389, AiPolicy.Off, (Guid?)null), (saved.Protocol, saved.Port, saved.AiPolicy, saved.ProxyId));
        Assert.Equal([bastion.Id], saved.JumpChain);

        ed = new HostEditorViewModel(s.Hosts, s.Identities, s.Proxies, saved);
        await ed.LoadAsync(Ct);
        Assert.True(ed.IsRdp);
        ed.ProtocolIsSsh = true;
        Assert.Equal("22", ed.Port);
    }

    [Fact]
    public async Task Host_list_opens_rdp_hosts_in_the_remote_desktop_tab_and_never_in_sftp()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var s = t.Storage;
        await s.Hosts.SaveAsync(new Host { Label = "win", Hostname = "10.0.0.9", Port = 3389, Protocol = HostProtocol.Rdp, AiPolicy = AiPolicy.Trusted }, Ct);
        await s.Hosts.SaveAsync(new Host { Label = "linux", Hostname = "10.0.0.5" }, Ct);
        var list = new HostListViewModel(s.Hosts, s.Identities, s.Proxies, new FakeUi());
        await list.LoadAsync(Ct);
        var win = list.Rows.Single(r => r.Name == "win");
        Assert.Equal("rdp://10.0.0.9", win.Address);
        Assert.Equal("—", win.AiPolicyText);
        var opened = new List<SessionKind>();
        list.ConnectRequested += (_, k) => opened.Add(k);
        list.SelectedRow = win;
        list.ConnectCommand.Execute(null);
        Assert.False(list.OpenSftpCommand.CanExecute(null));
        list.SelectedRow = list.Rows.Single(r => r.Name == "linux");
        Assert.True(list.OpenSftpCommand.CanExecute(null));
        list.ConnectCommand.Execute(null);
        Assert.Equal([SessionKind.Rdp, SessionKind.Terminal], opened);
    }

    // ------------------------------------------------------------------ review findings: connect lifecycle

    static async Task<(TestStore Store, Host Win, RdpServices Services)> TunnelledHostAsync()
    {
        var t = await TestStore.OpenAsync();
        var bastion = new Host { Label = "bastion", Hostname = "bastion.example.com" };
        var win = new Host { Label = "win", Hostname = "10.0.3.50", Port = 3389, Protocol = HostProtocol.Rdp, JumpChain = [bastion.Id] };
        await t.Storage.Hosts.SaveAsync(bastion, Ct);
        await t.Storage.Hosts.SaveAsync(win, Ct);
        var dir = Path.Combine(t.Storage.Paths.DataDirectory, "rdp");
        var launcher = new RdpLauncher(AppPlatform.MacOS, _ => true, _ => null, (_, _) => true);
        return (t, win, new RdpServices(t.Storage.Hosts, t.Storage.Identities, launcher, dir));
    }

    [Fact]
    public async Task Closing_the_tab_while_connecting_leaves_no_tunnel_file_or_launch()
    {
        Loc.Instance.SetCulture("en-US");
        var (t, win, services) = await TunnelledHostAsync();
        await using var _ = t;
        var launched = 0;
        services = services with { Launcher = new RdpLauncher(AppPlatform.MacOS, _ => true, _ => null, (_, _) => { launched++; return true; }) };
        var gate = new TaskCompletionSource();
        var connector = new GatedConnector(gate.Task);
        var tab = new RdpSessionViewModel(connector, new FakeUi(), win.Id, "win", "bastion", services);

        var connecting = tab.ConnectAsync();
        await tab.DisposeAsync();      // user closes the tab during the SSH handshake
        gate.SetResult();              // ... and the handshake completes afterwards
        await connecting;

        Assert.True(connector.Connection!.Disposed);
        Assert.True(connector.Connection.Forward!.Disposed);
        Assert.Equal(0, launched);
        Assert.Null(tab.RdpFilePath);
        Assert.False(Directory.Exists(services.FileDirectory) && Directory.EnumerateFiles(services.FileDirectory).Any());
    }

    [Fact]
    public async Task A_dropped_connection_while_opening_the_tunnel_fails_the_tab_and_releases_it()
    {
        Loc.Instance.SetCulture("en-US");
        var (t, win, services) = await TunnelledHostAsync();
        await using var _ = t;
        var connector = new GatedConnector(Task.CompletedTask, new MySSH.Ssh.SshConnectionLostException("The SSH connection closed while opening the tunnel"));
        var tab = new RdpSessionViewModel(connector, new FakeUi(), win.Id, "win", "bastion", services);
        await tab.ConnectAsync();
        Assert.Equal(SessionState.Failed, tab.State);
        Assert.Contains("tunnel", tab.StatusMessage, StringComparison.Ordinal);
        Assert.True(connector.Connection!.Disposed);
    }

    sealed class GatedConnector(Task gate, Exception? forwardError = null) : MySSH.Ssh.ISshConnector
    {
        public GatedConnection? Connection { get; private set; }

        public async Task<MySSH.Ssh.ISshConnection> ConnectAsync(Guid hostId, ConnectionOrigin origin, CancellationToken ct = default)
        {
            await gate; // ignores ct like a handshake that is already past cancellation
            return Connection = new GatedConnection(forwardError);
        }
    }

    sealed class GatedConnection(Exception? forwardError) : MySSH.Ssh.ISshConnection
    {
        public bool Disposed { get; private set; }
        public Forward? Forward { get; private set; }
        public Host Host { get; } = new() { Label = "bastion", Hostname = "bastion" };
        public ConnectionOrigin Origin => ConnectionOrigin.User;
        public MySSH.Ssh.SshConnectionState State => MySSH.Ssh.SshConnectionState.Connected;
        public event Action<MySSH.Ssh.SshConnectionStateChange>? StateChanged { add { } remove { } }
        public Task<ITerminalBackend> OpenShellAsync(int columns, int rows, string term = "xterm-256color", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MySSH.Ssh.ExecResult> ExecAsync(string command, MySSH.Ssh.ExecOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MySSH.Ssh.ISftpSession> OpenSftpAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MySSH.Ssh.ILocalForward> OpenLocalForwardAsync(string remoteHost, int remotePort, CancellationToken ct = default) =>
            forwardError is not null ? Task.FromException<MySSH.Ssh.ILocalForward>(forwardError) : Task.FromResult<MySSH.Ssh.ILocalForward>(Forward = new Forward());
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    sealed class Forward : MySSH.Ssh.ILocalForward
    {
        public bool Disposed { get; private set; }
        public string LocalHost => "127.0.0.1";
        public int LocalPort => 52011;
        public string RemoteHost => "10.0.3.50";
        public int RemotePort => 3389;
        public int ConnectionCount => 0;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
