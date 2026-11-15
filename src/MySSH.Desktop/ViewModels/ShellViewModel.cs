using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Storage;

namespace MySSH.Desktop.ViewModels;

public enum NotificationKind { Info, Success, Warning, Error }

public sealed record NotificationViewModel(NotificationKind Kind, string Title, string Message);

/// <summary>
/// Root view model: SideNav, the active page, startup prompts (vault / legacy import) and inline notifications.
/// Startup order (design §5): vault ready → legacy offer → host list → undecryptable-record scan.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    readonly Func<CancellationToken, Task<AppSession>> _openSession;
    AppSession? _session;

    public ShellViewModel(Func<CancellationToken, Task<AppSession>> openSession) => _openSession = openSession;

    public IReadOnlyList<NavItemViewModel> NavItems { get; } =
    [
        new(AppPage.Hosts, "virtual-machine", "nav.hosts"),
        new(AppPage.Identities, "password", "nav.identities"),
        new(AppPage.KnownHosts, "locked", "nav.knownHosts"),
        new(AppPage.Ai, "machine-learning", "nav.ai"),
        new(AppPage.Settings, "settings", "nav.settings"),
    ];

    public ObservableCollection<NotificationViewModel> Notifications { get; } = [];

    [ObservableProperty] public partial NavItemViewModel? SelectedNav { get; set; }
    [ObservableProperty] public partial HostListViewModel? Hosts { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; } = true;

    // ---- startup prompts (rendered as Carbon modals)
    [ObservableProperty] public partial StartupPrompt VaultPrompt { get; set; }
    [ObservableProperty] public partial string MasterPassword { get; set; } = "";
    [ObservableProperty] public partial string? VaultError { get; set; }
    [ObservableProperty] public partial int? LegacyOfferCount { get; set; }

    public bool IsVaultPromptOpen => VaultPrompt != StartupPrompt.None;
    public bool IsCreatingMasterPassword => VaultPrompt == StartupPrompt.CreateMasterPassword;
    public bool IsLegacyOfferOpen => LegacyOfferCount is not null && !IsVaultPromptOpen;
    public string LegacyOfferText => Loc.Instance.Format("legacy.found.body", ("count", LegacyOfferCount ?? 0));

    partial void OnVaultPromptChanged(StartupPrompt value)
    {
        OnPropertyChanged(nameof(IsVaultPromptOpen));
        OnPropertyChanged(nameof(IsCreatingMasterPassword));
        OnPropertyChanged(nameof(IsLegacyOfferOpen));
    }

    partial void OnLegacyOfferCountChanged(int? value)
    {
        OnPropertyChanged(nameof(IsLegacyOfferOpen));
        OnPropertyChanged(nameof(LegacyOfferText));
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        SelectedNav = NavItems[0];
        try
        {
            _session = await _openSession(ct);
            VaultPrompt = await _session.PrepareVaultAsync(ct);
            if (VaultPrompt == StartupPrompt.None) await AfterUnlockAsync(ct);
        }
        catch (VaultUnavailableException e)
        {
            Notify(NotificationKind.Error, Loc.Instance["common.error"], Loc.Instance.Format("storage.openFailed", ("error", e.Message)));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Notify(NotificationKind.Error, Loc.Instance["common.error"], Loc.Instance.Format("storage.openFailed", ("error", e.Message)));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    async Task SubmitMasterPasswordAsync(CancellationToken ct)
    {
        if (_session is null || string.IsNullOrEmpty(MasterPassword)) return;
        VaultError = null;
        IsBusy = true;
        try
        {
            if (VaultPrompt == StartupPrompt.CreateMasterPassword)
            {
                await _session.CreateMasterPasswordAsync(MasterPassword, ct);
            }
            else if (await _session.UnlockAsync(MasterPassword, ct) is not (VaultUnlockResult.Unlocked or VaultUnlockResult.AlreadyUnlocked))
            {
                VaultError = Loc.Instance["vault.unlock.wrong"];
                return;
            }
            MasterPassword = "";
            VaultPrompt = StartupPrompt.None;
            await AfterUnlockAsync(ct);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    async Task ImportLegacyAsync(CancellationToken ct)
    {
        if (_session is null) return;
        LegacyOfferCount = null;
        IsBusy = true;
        try
        {
            var r = await _session.ImportLegacyAsync(ct);
            Notify(NotificationKind.Success, Loc.Instance["legacy.doneTitle"], Loc.Instance.Format("legacy.done",
                ("imported", r.Hosts.Added + r.Hosts.Updated), ("identities", r.Identities.Added), ("proxies", r.Proxies.Added)));
            if (r.FailedRecords > 0)
            {
                var details = string.Join("; ", r.Issues.Where(i => i.Severity == ImportIssueSeverity.Error)
                    .Select(i => $"{i.RecordName ?? i.RecordId}: {i.Reason}"));
                Notify(NotificationKind.Warning, Loc.Instance["legacy.skippedTitle"], Loc.Instance.Format("legacy.skipped", ("count", r.FailedRecords), ("details", details)));
            }
            if (Hosts is not null) await Hosts.LoadAsync(ct);
        }
        catch (Exception e) when (e is LegacyImportException or IOException or InvalidOperationException)
        {
            Notify(NotificationKind.Error, Loc.Instance["common.error"], Loc.Instance.Format("legacy.failed", ("error", e.Message)));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    void DismissLegacyOffer() => LegacyOfferCount = null;

    [RelayCommand]
    void DismissNotification(NotificationViewModel n) => Notifications.Remove(n);

    async Task AfterUnlockAsync(CancellationToken ct)
    {
        var s = _session!;
        Hosts = new HostListViewModel(s.Storage.Hosts, s.Storage.Identities, s.Storage.Proxies);
        await Hosts.LoadAsync(ct);
        if (await s.DetectLegacyOfferAsync(ct) is { } offer) LegacyOfferCount = offer.RecordCount;
        var corrupt = await s.CountUndecryptableAsync(ct);
        if (corrupt > 0) Notify(NotificationKind.Warning, Loc.Instance["common.error"], Loc.Instance.Format("storage.corrupt", ("count", corrupt)));
    }

    void Notify(NotificationKind kind, string title, string message) => Notifications.Add(new(kind, title, message));

    public async ValueTask DisposeAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
    }
}
