using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.Desktop.Views;
using MySSH.Ssh.IntegrationTests;

namespace MySSH.Desktop.IntegrationTests;

/// <summary>
/// Windows remote desktop (option A) end to end: an RDP host behind an SSH jump host. A loopback echo server stands in for
/// the Windows box's port 3389; a fake launcher records what the system client would be given.
/// </summary>
[Collection(SshdGroup.Name)]
public sealed class RdpTabTests(SshdEnvironment env)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task Until(Func<bool> cond, string what, int timeoutMs = 15_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(30, Ct);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Rdp_host_behind_a_jump_host_gets_a_tunnel_an_rdp_file_and_the_system_client()
    {
        env.RequireSshd();
        Loc.Instance.SetCulture("zh-CN");
        var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var c = await echo.AcceptTcpClientAsync();
                    _ = Task.Run(async () => { using (c) { var s = c.GetStream(); var b = new byte[1024]; int n; while ((n = await s.ReadAsync(b)) > 0) await s.WriteAsync(b.AsMemory(0, n)); } });
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException) { }
        }, Ct);
        var dir = Directory.CreateTempSubdirectory("rdp-it-").FullName;
        try
        {
            await using var lab = new Lab(env);
            var jump = lab.AddHost("target"); // the SSH host that forwards
            var pw = lab.Ids.Add(new Identity { Label = "win-pw", Username = "Administrator", Kind = IdentityKind.Password }, new IdentitySecrets("Win-Secret-1", null, null));
            var win = lab.Hosts.Add(new Host
            {
                Label = "win-01", Hostname = "127.0.0.1", Port = ((IPEndPoint)echo.LocalEndpoint).Port, Protocol = HostProtocol.Rdp,
                IdentityId = pw.Id, JumpChain = [jump.Id],
            });
            var launched = new List<IReadOnlyList<string>>();
            var launcher = new RdpLauncher(AppPlatform.MacOS, p => p == "/Applications/Windows App.app", _ => null, (_, a) => { launched.Add(a); return true; });
            var ui = new FakeUi();
            var ws = new SessionsWorkspaceViewModel(() => lab.Connector(), ui) { Rdp = new RdpServices(lab.Hosts, lab.Ids, launcher, dir) };
            var page = new SessionsPage { DataContext = ws };
            var w = new Window { Width = 1000, Height = 600, Content = page };
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            w.Show();

            var row = new HostRowViewModel(win.Id, win.Label, "rdp://127.0.0.1", "Administrator", "Password", "target", AiPolicy.Off, "—", [], HostProtocol.Rdp);
            var tab = Assert.IsType<RdpSessionViewModel>(ws.Open(row, SessionKind.Rdp));
            await Until(() => tab.State == SessionState.Connected, "tunnel ready");

            // the system client was started with the .rdp file, which points at the tunnel and holds no password
            var args = Assert.Single(launched);
            var file = tab.RdpFilePath!;
            Assert.Equal(file, args[^1]);
            var text = await File.ReadAllTextAsync(file, Ct);
            Assert.Contains($"full address:s:127.0.0.1:{tab.LocalPort}", text, StringComparison.Ordinal);
            Assert.Contains("username:s:Administrator", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Win-Secret-1", text, StringComparison.Ordinal);
            Assert.NotNull(tab.TunnelText);
            Assert.Equal("Windows App", tab.ClientName);

            // bytes flow through the SSH tunnel to the "Windows" port
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, tab.LocalPort!.Value, Ct);
                var msg = Encoding.UTF8.GetBytes("RDP-远程桌面");
                await c.GetStream().WriteAsync(msg, Ct);
                var back = new byte[msg.Length];
                await c.GetStream().ReadExactlyAsync(back, Ct);
                Assert.Equal(msg, back);
            }

            await tab.CopyPasswordCommand.ExecuteAsync(null);
            Assert.Equal("Win-Secret-1", ui.Clipboard);
            Save(w, "p3-rdp-tab-light-zh");

            var port = tab.LocalPort!.Value;
            await ws.CloseTabCommand.ExecuteAsync(tab);
            Assert.False(File.Exists(file));
            using var after = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(async () => await after.ConnectAsync(IPAddress.Loopback, port, Ct));
            w.Close();
        }
        finally
        {
            echo.Stop();
            Loc.Instance.SetCulture("en-US");
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Missing_client_shows_the_install_hint()
    {
        Loc.Instance.SetCulture("zh-CN");
        var dir = Directory.CreateTempSubdirectory("rdp-it-").FullName;
        try
        {
            var hosts = new MySSH.Ssh.Tests.MemHosts(new Host { Label = "win-direct", Hostname = "10.9.9.9", Port = 3389, Protocol = HostProtocol.Rdp });
            var win = (await hosts.ListAsync(Ct)).Single();
            var launcher = new RdpLauncher(AppPlatform.MacOS, _ => false, _ => null, (_, _) => true);
            var ws = new SessionsWorkspaceViewModel(() => new NoConnector(), new FakeUi()) { Rdp = new RdpServices(hosts, new MySSH.Ssh.Tests.MemIdentities(), launcher, dir) };
            var w = new Window { Width = 1000, Height = 600, Content = new SessionsPage { DataContext = ws } };
            w.Show();
            var tab = (RdpSessionViewModel)ws.Open(new HostRowViewModel(win.Id, win.Label, "rdp://10.9.9.9", "", "", "—", AiPolicy.Off, "—", [], HostProtocol.Rdp), SessionKind.Rdp);
            await Until(() => tab.State == SessionState.Connected, "prepared");
            Assert.True(tab.IsClientMissing);
            Assert.True(tab.CanInstallWindowsApp);
            Assert.Null(tab.TunnelText); // direct: no tunnel
            Save(w, "p3-rdp-no-client-light-zh");
            await ws.DisposeAsync();
            w.Close();
        }
        finally
        {
            Loc.Instance.SetCulture("en-US");
            Directory.Delete(dir, true);
        }
    }

    sealed class NoConnector : MySSH.Ssh.ISshConnector
    {
        public Task<MySSH.Ssh.ISshConnection> ConnectAsync(Guid hostId, ConnectionOrigin origin, CancellationToken ct = default) =>
            throw new InvalidOperationException("no SSH needed for a direct RDP host");
    }

    static void Save(Window w, string name, [CallerFilePath] string here = "")
    {
        for (var i = 0; i < 30; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        var d = Path.Combine(Path.GetDirectoryName(here)!, ".work", "screenshots");
        Directory.CreateDirectory(d);
        w.CaptureRenderedFrame()!.Save(Path.Combine(d, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
