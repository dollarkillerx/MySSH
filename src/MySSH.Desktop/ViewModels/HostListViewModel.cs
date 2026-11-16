using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySSH.Desktop.Services;
using MySSH.Core;
using MySSH.Desktop.Localization;

namespace MySSH.Desktop.ViewModels;

/// <summary>One row of the Carbon data table on the Hosts page (design §8.4 wireframe).</summary>
public sealed record HostRowViewModel(
    Guid Id, string Name, string Address, string User, string Auth, string Via, AiPolicy AiPolicy, string AiPolicyText,
    IReadOnlyList<string> Tags)
{
    internal bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Address.Contains(query, StringComparison.OrdinalIgnoreCase)
        || User.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Via.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase));
}

public sealed partial class HostListViewModel : ObservableObject
{
    readonly IHostRepository _hosts;
    readonly IIdentityRepository _identities;
    readonly IProxyRepository _proxies;
    IReadOnlyList<HostRowViewModel> _all = [];

    readonly IUiServices _ui;

    public HostListViewModel(IHostRepository hosts, IIdentityRepository identities, IProxyRepository proxies, IUiServices? ui = null)
    {
        _hosts = hosts;
        _identities = identities;
        _proxies = proxies;
        _ui = ui ?? NullUiServices.Instance;
        Loc.Instance.CultureChanged += () => _ = LoadAsync();
    }

    public ObservableCollection<HostRowViewModel> Rows { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string CountText { get; set; } = "";
    [ObservableProperty] public partial HostRowViewModel? SelectedRow { get; set; }

    [ObservableProperty] public partial HostEditorViewModel? Editor { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    /// <summary>Raised when the user asks to open a session (double-click / Enter / "Connect").</summary>
    public event Action<HostRowViewModel, SessionKind>? ConnectRequested;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    async Task NewHostAsync()
    {
        Editor = new HostEditorViewModel(_hosts, _identities, _proxies, null);
        await Editor.LoadAsync();
    }

    [RelayCommand]
    async Task EditHostAsync(HostRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null || await _hosts.GetAsync(row.Id) is not { } host) return;
        Editor = new HostEditorViewModel(_hosts, _identities, _proxies, host);
        await Editor.LoadAsync();
    }

    [RelayCommand]
    async Task DuplicateHostAsync(HostRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null || await _hosts.GetAsync(row.Id) is not { } host) return;
        var copy = host with { Id = Guid.NewGuid(), Label = host.Label + " (2)", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        await _hosts.SaveAsync(copy);
        await LoadAsync();
    }

    [RelayCommand]
    async Task SaveEditorAsync()
    {
        if (Editor is null || await Editor.SaveAsync() is not { } saved) return;
        Editor = null;
        await LoadAsync();
        SelectedRow = Rows.FirstOrDefault(r => r.Id == saved.Id);
    }

    [RelayCommand]
    void CancelEditor() => Editor = null;

    [RelayCommand]
    async Task DeleteHostAsync(HostRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        Error = null;
        if (!await _ui.ConfirmAsync(Loc.Instance["servers.delete"], Loc.Instance.Format("servers.deleteConfirm", ("name", row.Name)), Loc.Instance["common.delete"], danger: true))
            return;
        try
        {
            await _hosts.DeleteAsync(row.Id);
            await LoadAsync();
        }
        catch (EntityInUseException e)
        {
            var users = _all.Where(r => e.ReferencedBy.Contains(r.Id)).Select(r => r.Name);
            Error = Loc.Instance.Format("hosts.inUse", ("name", row.Name), ("hosts", string.Join(", ", users)));
        }
    }

    [RelayCommand]
    void Connect(HostRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is not null) ConnectRequested?.Invoke(row, SessionKind.Terminal);
    }

    [RelayCommand]
    void OpenSftp(HostRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is not null) ConnectRequested?.Invoke(row, SessionKind.Sftp);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var hosts = await _hosts.ListAsync(ct);
            var identities = (await _identities.ListAsync(ct)).ToDictionary(i => i.Id);
            var proxies = (await _proxies.ListAsync(ct)).ToDictionary(p => p.Id);
            var byId = hosts.ToDictionary(h => h.Id);
            _all = [.. hosts.OrderBy(h => h.Label, StringComparer.CurrentCultureIgnoreCase)
                             .Select(h => ToRow(h, byId, identities, proxies))];
            ApplyFilter();
        }
        finally { IsLoading = false; }
    }

    internal static HostRowViewModel ToRow(Host h, IReadOnlyDictionary<Guid, Host> hosts,
        IReadOnlyDictionary<Guid, Identity> identities, IReadOnlyDictionary<Guid, ProxyProfile> proxies)
    {
        var loc = Loc.Instance;
        Identity? identity = h.IdentityId is { } iid && identities.TryGetValue(iid, out var i) ? i : null;
        var user = h.Username ?? identity?.Username ?? "";
        var auth = identity is null ? loc["auth.none"] : identity.Kind == IdentityKind.PrivateKey ? loc["auth.key"] : loc["auth.password"];

        var hops = new List<string>();
        if (h.ProxyId is { } pid && proxies.TryGetValue(pid, out var proxy))
            hops.Add($"{(proxy.Kind == ProxyKind.Socks5 ? "SOCKS5" : "HTTP")} {proxy.Hostname}:{proxy.Port}");
        foreach (var jid in h.JumpChain)
            hops.Add(hosts.TryGetValue(jid, out var j) ? j.Label : "?");
        var via = hops.Count == 0 ? "—" : string.Join(" → ", hops);

        var address = h.Port == 22 ? h.Hostname : $"{h.Hostname}:{h.Port}";
        return new HostRowViewModel(h.Id, h.Label, address, user, auth, via, h.AiPolicy, loc["aiPolicy." + h.AiPolicy], h.Tags);
    }

    void ApplyFilter()
    {
        var q = SearchText.Trim();
        var rows = q.Length == 0 ? _all : [.. _all.Where(r => r.Matches(q))];
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        IsEmpty = _all.Count == 0;
        CountText = Loc.Instance.Format("hosts.count", ("count", rows.Count));
    }
}
