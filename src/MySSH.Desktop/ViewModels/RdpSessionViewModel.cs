using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Ssh;

namespace MySSH.Desktop.ViewModels;

/// <param name="FileDirectory">Where .rdp files are written (owner-only; each tab deletes its file on close).</param>
public sealed record RdpServices(IHostRepository Hosts, IIdentityRepository Identities, RdpLauncher Launcher, string FileDirectory);

/// <summary>
/// A Windows remote desktop host (option A: the system RDP client). With a jump host the tab owns an SSH local forward
/// (127.0.0.1:random → host:3389 through the jump host's chain) for as long as it is open; the system client connects to it.
/// The password is never written to the .rdp file or a command line: "Copy password" puts it on the clipboard.
/// </summary>
public sealed partial class RdpSessionViewModel(ISshConnector connector, IUiServices ui, Guid hostId, string hostLabel, string via, RdpServices services)
    : SessionTabViewModel(connector, ui, hostId, hostLabel, via)
{
    ISshConnection? _connection;
    ILocalForward? _forward;
    string? _file;
    CancellationTokenSource? _attempt;

    public override SessionKind Kind => SessionKind.Rdp;

    [ObservableProperty] public partial string TargetText { get; set; } = "";
    /// <summary>"127.0.0.1:52011 → 10.0.0.9:3389 (via bastion)" while tunnelled; null for a direct connection.</summary>
    [ObservableProperty] public partial string? TunnelText { get; set; }
    [ObservableProperty] public partial string? ClientName { get; set; }
    [ObservableProperty] public partial bool IsClientMissing { get; set; }
    [ObservableProperty] public partial bool HasPassword { get; set; }
    [ObservableProperty] public partial string? Notice { get; set; }
    public bool CanInstallWindowsApp => IsClientMissing && services.Launcher.Platform == AppPlatform.MacOS;
    partial void OnIsClientMissingChanged(bool value) => OnPropertyChanged(nameof(CanInstallWindowsApp));
    public string MissingClientText => Loc.Instance["rdp.noClient." + services.Launcher.Platform];

    /// <summary>The local tunnel port while open (tests / status).</summary>
    public int? LocalPort => _forward?.LocalPort;
    public string? RdpFilePath => _file;

    public async Task ConnectAsync()
    {
        // Each attempt owns what it opens until it hands it to the tab. A newer attempt or closing the tab cancels it;
        // a cancelled attempt disposes its own connection / tunnel instead of storing them on a closed tab.
        var attempt = new CancellationTokenSource();
        Interlocked.Exchange(ref _attempt, attempt)?.Cancel();
        var ct = attempt.Token;
        await CloseAsync();
        if (ct.IsCancellationRequested) return;
        State = SessionState.Connecting;
        StatusMessage = Loc.Instance.Format("rdp.preparing", ("host", HostLabel));
        Notice = null;
        ISshConnection? conn = null;
        ILocalForward? forward = null;
        try
        {
            var host = await services.Hosts.GetAsync(HostId, ct) ?? throw new InvalidOperationException(Loc.Instance["rdp.hostMissing"]);
            var identity = host.IdentityId is { } iid ? await services.Identities.GetAsync(iid, ct) : null;
            var user = host.Username ?? identity?.Username;

            RdpTarget target;
            string? tunnel = null;
            if (host.JumpChain.Count > 0)
            {
                // The last jump host is the SSH host that forwards; its own jump chain / proxy apply as usual.
                conn = await Connector.ConnectAsync(host.JumpChain[^1], ConnectionOrigin.User, ct);
                forward = await conn.OpenLocalForwardAsync(host.Hostname, host.Port, ct);
                target = new RdpTarget(forward.LocalHost, forward.LocalPort, user, host.Label);
                tunnel = Loc.Instance.Format("rdp.tunnel", ("local", $"{forward.LocalHost}:{forward.LocalPort}"),
                    ("target", $"{host.Hostname}:{host.Port}"), ("via", Via));
            }
            else
            {
                target = new RdpTarget(host.Hostname, host.Port, user, host.Label);
            }
            ct.ThrowIfCancellationRequested();

            // Hand over to the tab (no await between the check above and here: closing cannot interleave).
            _connection = conn;
            _forward = forward;
            (conn, forward) = (null, null);
            if (_connection is not null) _connection.StateChanged += OnConnectionStateChanged;
            HasPassword = identity?.Kind == IdentityKind.Password;
            TargetText = host.Port == Host.DefaultRdpPort ? host.Hostname : $"{host.Hostname}:{host.Port}";
            TunnelText = tunnel;
            _file = RdpFile.Write(services.FileDirectory, SessionId, target);
            State = SessionState.Connected;
            StatusMessage = null;
            Launch();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Closed or superseded: nothing to show.
        }
        catch (Exception e) when (e is SshConnectException or SshConnectionLostException or OperationCanceledException or IOException
                                       or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            await CloseAsync();
            if (ct.IsCancellationRequested) return;
            State = SessionState.Failed;
            StatusMessage = Loc.Instance.Format("rdp.failed", ("error", Describe(e)));
        }
        finally
        {
            if (forward is not null) await forward.DisposeAsync();
            if (conn is not null) await conn.DisposeAsync();
        }
    }

    void OnConnectionStateChanged(SshConnectionStateChange change)
    {
        if (change.Current == SshConnectionState.Lost)
            OnUi(() =>
            {
                if (State != SessionState.Connected) return;
                State = SessionState.Lost;
                StatusMessage = Loc.Instance.Format("ssh.err.Lost", ("message", change.Reason ?? ""));
            });
    }

    /// <summary>(Re)opens the system client on the prepared .rdp file.</summary>
    [RelayCommand]
    void Launch()
    {
        if (_file is null) return;
        var client = services.Launcher.Launch(_file);
        IsClientMissing = client is null;
        ClientName = client?.Name;
        Notice = client is null ? null : Loc.Instance.Format("rdp.launched", ("client", client.Name));
    }

    [RelayCommand]
    async Task CopyPasswordAsync()
    {
        var host = await services.Hosts.GetAsync(HostId);
        if (host?.IdentityId is not { } iid || (await services.Identities.GetSecretsAsync(iid))?.Password is not { Length: > 0 } pw)
        {
            Notice = Loc.Instance["rdp.noPassword"];
            return;
        }
        await Ui.CopyToClipboardAsync(pw);
        Notice = Loc.Instance["rdp.passwordCopied"];
    }

    [RelayCommand]
    Task InstallClientAsync() => Ui.OpenUriAsync(RdpLauncher.WindowsAppStoreUrl);

    protected override Task ReconnectAsync() => ConnectAsync();

    async Task CloseAsync()
    {
        if (_forward is { } f) { _forward = null; await f.DisposeAsync(); }
        if (_connection is { } c)
        {
            _connection = null;
            c.StateChanged -= OnConnectionStateChanged;
            await c.DisposeAsync();
        }
        if (_file is { } path)
        {
            _file = null;
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _attempt, null)?.Cancel(); // a connect in flight cleans up after itself
        await CloseAsync();
    }
}
