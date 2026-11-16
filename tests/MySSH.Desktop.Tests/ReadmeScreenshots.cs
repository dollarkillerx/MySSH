using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.Ssh;
using MySSH.Storage;
using MySSH.Tools;

namespace MySSH.Desktop.Tests;

/// <summary>
/// Renders the README screenshots (docs/screenshots) from demo data: fake SSH shell / SFTP / tunnel, no real machine,
/// user name or file shows up. Skipped unless MYSSH_README_SCREENSHOTS=1:
/// <c>MYSSH_README_SCREENSHOTS=1 dotnet test tests/MySSH.Desktop.Tests -- --filter-class MySSH.Desktop.Tests.ReadmeScreenshots</c>
/// </summary>
[Collection("Loc")]
public sealed class ReadmeScreenshots : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("myssh-readme-").FullName;
    static readonly string s_out = Path.Combine(RepoRoot(), "docs", "screenshots");

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public async Task Render()
    {
        if (Environment.GetEnvironmentVariable("MYSSH_README_SCREENSHOTS") != "1")
            Assert.Skip("Set MYSSH_README_SCREENSHOTS=1 to regenerate docs/screenshots.");
        Directory.CreateDirectory(s_out);
        Loc.Instance.SetCulture("en-US");

        var o = new StorageOptions
        {
            Paths = new AppPaths { DataDirectory = Path.Combine(_root, "MySSH.Desktop"), LegacyDataDirectory = Path.Combine(_root, "none") },
            OsKeyStore = new MemKeyStore(),
            UsePlatformKeyStore = false,
            Argon2 = new Argon2Parameters(1024, 1, 1),
        };
        AppSession? session = null;
        // Shown on the MCP page: a neutral path instead of the per-user temp folder.
        var socketDir = "/tmp/myssh-demo";
        if (Directory.Exists(socketDir)) Directory.Delete(socketDir, true);
        await using var vm = new ShellViewModel(async ct => session = await AppSession.OpenAsync(o, ct))
        {
            Connector = new DemoConnector(),
            ConfigureMcp = m => m with { LocalSocketPath = Path.Combine(socketDir, "mcp.sock"), Port = 51428 },
        };
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        w.Show();
        await vm.InitializeAsync();
        await SeedAsync(session!.Storage);
        await vm.Hosts!.LoadAsync();
        vm.Notifications.Clear();
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark; // InitializeAsync applied the saved (system) theme
        Pump();
        Save(w, "hosts-dark");

        // Host editor (light): jump host + AI policy
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        await vm.Hosts.EditHostCommand.ExecuteAsync(Row(vm, "db-primary"));
        Pump();
        // Scroll the editor to the jump host and AI / MCP sections.
        var panel = w.GetVisualDescendants().OfType<MySSH.Desktop.Views.HostEditorPanel>().Single();
        var scroll = panel.GetVisualDescendants().OfType<ScrollViewer>().First();
        var jump = scroll.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == Loc.Instance["serverForm.jumpHost"]);
        scroll.Offset = new Vector(0, jump.TranslatePoint(default, (Visual)scroll.Content!)!.Value.Y - 16);
        Pump();
        Save(w, "host-editor-light");
        vm.Hosts.CancelEditorCommand.Execute(null);

        // Sessions (dark): terminal, SFTP opened from it, RDP through the bastion
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        vm.Sessions!.Rdp = vm.Sessions.Rdp! with
        {
            Launcher = new RdpLauncher(AppPlatform.MacOS, fileExists: p => p.EndsWith("Windows App.app", StringComparison.Ordinal), start: (_, _) => true),
        };
        vm.SelectedNav = vm.NavItems.First(n => n.Page == AppPage.Sessions);
        var term = (TerminalSessionViewModel)vm.Sessions.Open(Row(vm, "web-01"), SessionKind.Terminal);
        await Settle(() => term.IsConnected);
        await Task.Delay(300);
        Pump();
        Save(w, "terminal-dark");

        var sftp = vm.Sessions.OpenSftpFor(term);
        await Settle(() => sftp.IsConnected && sftp.Entries.Count > 0);
        Save(w, "sftp-dark");

        var rdp = (RdpSessionViewModel)vm.Sessions.Open(Row(vm, "win-build"), SessionKind.Rdp);
        await Settle(() => rdp.IsConnected && rdp.ClientName is not null);
        Save(w, "rdp-dark");

        // MCP server (light) and a high-risk approval (dark)
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        vm.SelectedNav = vm.NavItems.First(n => n.Page == AppPage.Ai);
        await vm.AiMcp!.Mcp.SetEnabledCommandForTests(true);
        await Task.Delay(400);
        Pump();
        Assert.True(vm.Mcp!.IsRunning, vm.Mcp.StartError);
        Save(w, "mcp-light");

        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        vm.SelectedNav = vm.NavItems.First(n => n.Page == AppPage.Sessions);
        vm.Sessions.SelectedTab = term;
        Pump();
        var web = (await session.Storage.Hosts.ListAsync()).First(h => h.Label == "web-01");
        var pending = w.RequestAsync(new ApprovalRequest(Guid.NewGuid(), new ToolActor(ConnectionOrigin.Mcp, "mcp:claude-code", "claude-code (MCP)"),
            new ToolCatalog().Find("run_command")!, web, "sudo systemctl restart nginx", null,
            "High-risk rule matched: systemctl restart", true, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120),
            ApprovalReason.HighRisk, "systemctl restart"), TestContext.Current.CancellationToken);
        Pump();
        Save(w, "approval-dark");
        w.FindControl<MySSH.UI.Carbon.Modal>("ApprovalModal")!.Close(MySSH.UI.Carbon.ModalResult.Secondary);
        await pending;

        await vm.AiMcp.Mcp.SetEnabledCommandForTests(false);
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        w.Close();
        Directory.Delete(socketDir, true);
    }

    static HostRowViewModel Row(ShellViewModel vm, string label) => vm.Hosts!.Rows.First(r => r.Name == label);

    static async Task Settle(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            Pump();
            await Task.Delay(20);
        }
        Assert.True(done());
        Pump();
    }

    static async Task SeedAsync(MySshStorage s)
    {
        var deployKey = new Identity { Label = "deploy key", Username = "deploy", Kind = IdentityKind.PrivateKey };
        await s.Identities.SaveAsync(deployKey, new IdentitySecrets(null, "-----BEGIN OPENSSH PRIVATE KEY-----\ndemo\n-----END OPENSSH PRIVATE KEY-----\n", null));
        var admin = new Identity { Label = "admin password", Username = "admin", Kind = IdentityKind.Password };
        await s.Identities.SaveAsync(admin, new IdentitySecrets("demo-password", null, null));
        var aiUser = new Identity { Label = "ai-readonly", Username = "ai", Kind = IdentityKind.PrivateKey };
        await s.Identities.SaveAsync(aiUser, new IdentitySecrets(null, "-----BEGIN OPENSSH PRIVATE KEY-----\ndemo\n-----END OPENSSH PRIVATE KEY-----\n", null));
        var socks = new ProxyProfile { Label = "office SOCKS5", Kind = ProxyKind.Socks5, Hostname = "proxy.example.com", Port = 1080 };
        await s.Proxies.SaveAsync(socks, null);

        var bastion = new Host { Label = "bastion", Hostname = "bastion.example.com", Username = "deploy", IdentityId = deployKey.Id, Tags = ["prod"] };
        Host[] hosts =
        [
            bastion,
            new() { Label = "web-01", Hostname = "10.0.1.11", Username = "deploy", IdentityId = deployKey.Id, JumpChain = [bastion.Id], AiPolicy = AiPolicy.Trusted, Tags = ["prod", "web"] },
            new() { Label = "web-02", Hostname = "10.0.1.12", Username = "deploy", IdentityId = deployKey.Id, JumpChain = [bastion.Id], AiPolicy = AiPolicy.Trusted, Tags = ["prod", "web"] },
            new() { Label = "db-primary", Hostname = "10.0.2.21", Username = "deploy", IdentityId = deployKey.Id, JumpChain = [bastion.Id], AiPolicy = AiPolicy.ReadOnly,
                AiIdentityId = aiUser.Id, AiCommandAllowlist = ["pg_lsclusters", "systemctl status postgresql"], Tags = ["prod", "db"] },
            new() { Label = "staging", Hostname = "staging.example.com", Port = 2222, Username = "deploy", IdentityId = deployKey.Id, AiPolicy = AiPolicy.FullAccess, Tags = ["staging"] },
            new() { Label = "lab-pi", Hostname = "192.168.1.40", Username = "pi", IdentityId = admin.Id, ProxyId = socks.Id, AiPolicy = AiPolicy.Ask },
            new() { Label = "win-build", Hostname = "10.0.3.50", Port = Host.DefaultRdpPort, Protocol = HostProtocol.Rdp, Username = "builder", IdentityId = admin.Id, JumpChain = [bastion.Id] },
        ];
        foreach (var h in hosts) await s.Hosts.SaveAsync(h);
    }

    static void Pump()
    {
        for (var i = 0; i < 30; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    }

    static void Save(Window w, string name)
    {
        Pump();
        w.CaptureRenderedFrame()!.Save(Path.Combine(s_out, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    // ------------------------------------------------------------------ demo SSH

    sealed class MemKeyStore : IOsKeyStore
    {
        byte[]? _k;
        public string Kind => "test-memory";
        public bool IsAvailable => true;
        public byte[]? Load() => _k?.ToArray();
        public void Save(ReadOnlySpan<byte> key) => _k = key.ToArray();
        public void Delete() => _k = null;
    }

    sealed class DemoConnector : ISshConnector
    {
        public Task<ISshConnection> ConnectAsync(Guid hostId, ConnectionOrigin origin, CancellationToken ct = default) =>
            Task.FromResult<ISshConnection>(new DemoConnection(origin));
    }

    sealed class DemoConnection(ConnectionOrigin origin) : ISshConnection
    {
        public Host Host { get; } = new() { Label = "demo", Hostname = "demo" };
        public ConnectionOrigin Origin => origin;
        public SshConnectionState State => SshConnectionState.Connected;
        public event Action<SshConnectionStateChange>? StateChanged { add { } remove { } }
        public Task<ITerminalBackend> OpenShellAsync(int columns, int rows, string term = "xterm-256color", CancellationToken ct = default) =>
            Task.FromResult<ITerminalBackend>(new DemoShell());
        public Task<ExecResult> ExecAsync(string command, ExecOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ISftpSession> OpenSftpAsync(CancellationToken ct = default) => Task.FromResult<ISftpSession>(new DemoSftp());
        public Task<ILocalForward> OpenLocalForwardAsync(string remoteHost, int remotePort, CancellationToken ct = default) =>
            Task.FromResult<ILocalForward>(new DemoForward(remoteHost, remotePort));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class DemoForward(string host, int port) : ILocalForward
    {
        public string LocalHost => "127.0.0.1";
        public int LocalPort => 52011;
        public string RemoteHost => host;
        public int RemotePort => port;
        public int ConnectionCount => 1;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class DemoShell : ITerminalBackend
    {
        const string Prompt = "\e[1;32mdeploy@web-01\e[0m:\e[1;34m~/app\e[0m$ ";
        static readonly string s_screen = string.Join("\r\n",
            "Welcome to Ubuntu 24.04.1 LTS (GNU/Linux 6.8.0-45-generic x86_64)",
            "",
            "Last login: Mon Nov 16 09:12:44 2026 from 10.0.0.2",
            "\e]7;file://web-01/home/deploy/app\a" + Prompt + "uptime",
            " 14:02:11 up 42 days,  3:17,  1 user,  load average: 0.08, 0.12, 0.09",
            Prompt + "df -h /",
            "Filesystem      Size  Used Avail Use% Mounted on",
            "/dev/sda1        78G   31G   44G  42% /",
            Prompt + "ls -l",
            "total 24",
            "drwxr-xr-x 2 deploy deploy 4096 Nov 12 18:40 \e[1;34mconfig\e[0m",
            "-rw-r--r-- 1 deploy deploy 1873 Nov 12 18:40 docker-compose.yml",
            "drwxr-xr-x 3 deploy deploy 4096 Nov 15 22:05 \e[1;34mlogs\e[0m",
            "-rwxr-xr-x 1 deploy deploy  612 Nov 10 11:02 \e[1;32mdeploy.sh\e[0m",
            "drwxr-xr-x 5 deploy deploy 4096 Nov 14 16:31 \e[1;34msrc\e[0m",
            Prompt + "systemctl status nginx --no-pager | head -3",
            "\e[1;32m●\e[0m nginx.service - A high performance web server and a reverse proxy server",
            "     Loaded: loaded (/usr/lib/systemd/system/nginx.service; \e[1;32menabled\e[0m; preset: \e[1;32menabled\e[0m)",
            "     Active: \e[1;32mactive (running)\e[0m since Sat 2026-11-14 08:00:03 UTC; 2 days ago",
            Prompt);

        public event Action<ReadOnlyMemory<byte>>? DataReceived;
        public event Action<TerminalExit>? Exited { add { } remove { } }
        bool _sent;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;

        public void Resize(int columns, int rows)
        {
            if (_sent) return;
            _sent = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                DataReceived?.Invoke(Encoding.UTF8.GetBytes(s_screen));
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class DemoSftp : ISftpSession
    {
        static readonly DateTimeOffset s_t = new DateTime(2026, 11, 14, 16, 31, 0, DateTimeKind.Local);
        public string HomeDirectory => "/home/deploy";
        public Task<string> RealPathAsync(string path, CancellationToken ct = default) => Task.FromResult(path);

        public Task<IReadOnlyList<SftpEntry>> ListAsync(string path, CancellationToken ct = default)
        {
            SftpEntry D(string n, int days) => new(n, SftpPath.Combine(path, n), SftpEntryKind.Directory, 4096, s_t.AddDays(-days), "drwxr-xr-x");
            SftpEntry F(string n, long size, int days, string perm = "-rw-r--r--") => new(n, SftpPath.Combine(path, n), SftpEntryKind.File, size, s_t.AddDays(-days).AddMinutes(-days * 47), perm);
            return Task.FromResult<IReadOnlyList<SftpEntry>>(
            [
                D("config", 2), D("logs", 0), D("src", 0),
                F(".env.example", 412, 9), F("deploy.sh", 612, 4, "-rwxr-xr-x"), F("docker-compose.yml", 1873, 2),
                F("README.md", 5_120, 6), F("release-2026.11.tar.gz", 48_211_968, 1),
            ]);
        }

        public Task<SftpEntry?> StatAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<SftpEntry?>(new(Path.GetFileName(path), path, SftpEntryKind.Directory, 4096, s_t, "drwxr-xr-x"));

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(SftpEntry entry, bool recursive = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RenameAsync(SftpEntry entry, string newPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> ReadAllTextAsync(string path, long maxBytes = 1024 * 1024, CancellationToken ct = default) => throw new NotSupportedException();
        public Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UploadAsync(Stream source, string remotePath, bool overwrite, Action<long>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DownloadAsync(string remotePath, Stream destination, Action<long>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
