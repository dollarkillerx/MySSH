using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;

namespace MySSH.Desktop.ViewModels;

public sealed record KnownHostRowViewModel(string HostPort, string KeyType, string Fingerprint, string FirstSeen, string LastSeen);

/// <summary>Trusted host keys (TOFU store, design §5.2). Removing an entry makes the next connect ask again.</summary>
public sealed partial class KnownHostsViewModel(IKnownHostStore store, IUiServices ui) : ObservableObject
{
    public ObservableCollection<KnownHostRowViewModel> Rows { get; } = [];
    [ObservableProperty] public partial KnownHostRowViewModel? SelectedRow { get; set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Rows.Clear();
        foreach (var e in (await store.ListAsync(ct)).OrderBy(e => e.HostPort, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.KeyType, StringComparer.Ordinal))
            Rows.Add(new KnownHostRowViewModel(e.HostPort, e.KeyType, e.Sha256Fingerprint,
                e.FirstSeen.ToLocalTime().ToString("g", Loc.Instance.Culture), e.LastSeen.ToLocalTime().ToString("g", Loc.Instance.Culture)));
    }

    [RelayCommand]
    async Task RemoveAsync(KnownHostRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        if (!await ui.ConfirmAsync(Loc.Instance["knownHosts.remove"], Loc.Instance.Format("knownHosts.removeConfirm", ("host", row.HostPort)), Loc.Instance["knownHosts.remove"], danger: true))
            return;
        await store.RemoveAsync(row.HostPort, row.KeyType);
        await LoadAsync();
    }

    [RelayCommand]
    Task CopyFingerprintAsync(KnownHostRowViewModel? row) => (row ?? SelectedRow) is { } r ? ui.CopyToClipboardAsync(r.Fingerprint) : Task.CompletedTask;

    internal static string Format(DateTimeOffset t) => t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
