using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public HostListViewModel(IHostRepository hosts, IIdentityRepository identities, IProxyRepository proxies)
    {
        _hosts = hosts;
        _identities = identities;
        _proxies = proxies;
        Loc.Instance.CultureChanged += () => _ = LoadAsync();
    }

    public ObservableCollection<HostRowViewModel> Rows { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string CountText { get; set; } = "";
    [ObservableProperty] public partial HostRowViewModel? SelectedRow { get; set; }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

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
