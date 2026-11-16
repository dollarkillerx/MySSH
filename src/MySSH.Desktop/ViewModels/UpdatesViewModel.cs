using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;

namespace MySSH.Desktop.ViewModels;

public enum UpdateState { NotInstalled, Idle, Checking, UpToDate, Downloading, Ready, Failed }

/// <summary>
/// Settings → Updates: version, "Check for updates", download progress, "Restart to update". A background check runs once
/// after startup (unless turned off); it stays silent unless an update is ready.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    readonly IAppUpdater _updater;
    readonly Action _quit;

    /// <param name="quit">Quits the app the normal way (Velopack installs after the process exits).</param>
    public UpdatesViewModel(IAppUpdater updater, Action quit)
    {
        _updater = updater;
        _quit = quit;
        if (!updater.IsInstalled) State = UpdateState.NotInstalled;
        else if (updater.PendingVersion is { } pending)
        {
            NewVersion = pending;
            State = UpdateState.Ready;
        }
        else State = UpdateState.Idle;
    }

    public string CurrentVersion => Loc.Instance.Format("updates.current", ("version", _updater.CurrentVersion));

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(IsReady), nameof(IsBusy), nameof(IsError))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial UpdateState State { get; private set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] public partial string? NewVersion { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] public partial int Progress { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] public partial string? Error { get; private set; }

    public bool IsReady => State == UpdateState.Ready;
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;
    public bool IsError => State == UpdateState.Failed;

    public string StatusText => State switch
    {
        UpdateState.NotInstalled => Loc.Instance["updates.notInstalled"],
        UpdateState.Checking => Loc.Instance["updates.checking"],
        UpdateState.UpToDate => Loc.Instance["updates.upToDate"],
        UpdateState.Downloading => Loc.Instance.Format("updates.downloading", ("version", NewVersion ?? ""), ("percent", Progress)),
        UpdateState.Ready => Loc.Instance.Format("updates.ready", ("version", NewVersion ?? "")),
        UpdateState.Failed => Loc.Instance.Format("updates.failed", ("error", Error ?? "")),
        _ => "",
    };

    /// <summary>Raised (on the UI thread) when an update finished downloading.</summary>
    public event Action<string>? UpdateReady;

    bool CanCheck() => State is UpdateState.Idle or UpdateState.UpToDate or UpdateState.Failed;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    Task CheckAsync(CancellationToken ct) => RunAsync(background: false, ct);

    /// <summary>The startup check: failures (offline, rate-limited) are not shown.</summary>
    public Task CheckInBackgroundAsync(CancellationToken ct = default) =>
        State == UpdateState.Idle ? RunAsync(background: true, ct) : Task.CompletedTask;

    async Task RunAsync(bool background, CancellationToken ct)
    {
        State = UpdateState.Checking;
        Error = null;
        try
        {
            var version = await _updater.CheckAsync(ct);
            if (version is null)
            {
                State = background ? UpdateState.Idle : UpdateState.UpToDate;
                return;
            }
            NewVersion = version;
            Progress = 0;
            State = UpdateState.Downloading;
            var ui = SynchronizationContext.Current;
            await _updater.DownloadAsync(p =>
            {
                if (ui is null) Progress = p;
                else ui.Post(_ => Progress = p, null);
            }, ct);
            State = UpdateState.Ready;
            UpdateReady?.Invoke(version);
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Idle;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or TimeoutException or NotSupportedException or ArgumentException)
        {
            Error = e.Message;
            State = background ? UpdateState.Idle : UpdateState.Failed;
        }
    }

    [RelayCommand]
    void Restart()
    {
        if (State != UpdateState.Ready) return;
        _updater.ApplyAfterExit();
        _quit();
    }
}
