using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySSH.Core;
using MySSH.Desktop.Localization;

namespace MySSH.Desktop.ViewModels;

public enum AuthChoice { SavedIdentity, NewPassword, NewPrivateKey }
public enum ProxyChoice { None, Saved, New }

public sealed record IdentityOption(Guid Id, string Label, IdentityKind Kind)
{
    public override string ToString() => Label;
}

public sealed record ProxyOption(Guid Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record HostOption(Guid Id, string Label, string Address)
{
    public override string ToString() => $"{Label} ({Address})";
}

/// <summary>
/// Side-panel editor for a host (design §8.4): general, authentication (saved identity or new credentials),
/// network (proxy + ordered jump chain), AI/MCP policy and notes. Creates identities / proxies on save when asked to.
/// </summary>
public sealed partial class HostEditorViewModel : ObservableObject
{
    readonly IHostRepository _hosts;
    readonly IIdentityRepository _identities;
    readonly IProxyRepository _proxies;
    readonly Host? _original;
    IReadOnlyDictionary<Guid, Host> _allHosts = new Dictionary<Guid, Host>();

    public HostEditorViewModel(IHostRepository hosts, IIdentityRepository identities, IProxyRepository proxies, Host? existing)
    {
        _hosts = hosts;
        _identities = identities;
        _proxies = proxies;
        _original = existing;
    }

    public bool IsNew => _original is null;
    public Guid HostId => _original?.Id ?? _newId;
    readonly Guid _newId = Guid.NewGuid();

    public string Heading => IsNew ? Loc.Instance["hosts.add"] : Loc.Instance["servers.edit"];

    // ---- general
    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial string Hostname { get; set; } = "";
    [ObservableProperty] public partial string Port { get; set; } = "22";
    [ObservableProperty] public partial string Username { get; set; } = "";
    [ObservableProperty] public partial string Tags { get; set; } = "";
    [ObservableProperty] public partial string Notes { get; set; } = "";

    // ---- authentication
    public ObservableCollection<IdentityOption> IdentityOptions { get; } = [];
    [ObservableProperty] public partial AuthChoice Auth { get; set; } = AuthChoice.NewPassword;
    [ObservableProperty] public partial IdentityOption? SelectedIdentity { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string PrivateKey { get; set; } = "";
    [ObservableProperty] public partial string Passphrase { get; set; } = "";
    public bool IsSavedIdentity => Auth == AuthChoice.SavedIdentity;
    public bool IsNewPassword => Auth == AuthChoice.NewPassword;
    public bool IsNewPrivateKey => Auth == AuthChoice.NewPrivateKey;
    // Radio-button friendly (AOT-safe, no enum converters)
    public bool AuthIsSaved { get => Auth == AuthChoice.SavedIdentity; set { if (value) Auth = AuthChoice.SavedIdentity; } }
    public bool AuthIsPassword { get => Auth == AuthChoice.NewPassword; set { if (value) Auth = AuthChoice.NewPassword; } }
    public bool AuthIsKey { get => Auth == AuthChoice.NewPrivateKey; set { if (value) Auth = AuthChoice.NewPrivateKey; } }
    partial void OnAuthChanged(AuthChoice value)
    {
        OnPropertyChanged(nameof(AuthIsSaved));
        OnPropertyChanged(nameof(AuthIsPassword));
        OnPropertyChanged(nameof(AuthIsKey));
        AuthError = null;
        OnPropertyChanged(nameof(IsSavedIdentity));
        OnPropertyChanged(nameof(IsNewPassword));
        OnPropertyChanged(nameof(IsNewPrivateKey));
    }

    // ---- network
    public ObservableCollection<ProxyOption> ProxyOptions { get; } = [];
    [ObservableProperty] public partial ProxyChoice Proxy { get; set; } = ProxyChoice.None;
    [ObservableProperty] public partial ProxyOption? SelectedProxy { get; set; }
    [ObservableProperty] public partial ProxyKind NewProxyKind { get; set; } = ProxyKind.Socks5;
    [ObservableProperty] public partial string NewProxyHost { get; set; } = "";
    [ObservableProperty] public partial string NewProxyPort { get; set; } = "1080";
    [ObservableProperty] public partial string NewProxyUsername { get; set; } = "";
    [ObservableProperty] public partial string NewProxyPassword { get; set; } = "";
    public bool IsSavedProxy => Proxy == ProxyChoice.Saved;
    public bool IsNewProxy => Proxy == ProxyChoice.New;
    public bool ProxyIsNone { get => Proxy == ProxyChoice.None; set { if (value) Proxy = ProxyChoice.None; } }
    public bool ProxyIsSaved { get => Proxy == ProxyChoice.Saved; set { if (value) Proxy = ProxyChoice.Saved; } }
    public bool ProxyIsNew { get => Proxy == ProxyChoice.New; set { if (value) Proxy = ProxyChoice.New; } }
    public IReadOnlyList<ProxyKind> ProxyKinds { get; } = [ProxyKind.Socks5, ProxyKind.Http];
    partial void OnProxyChanged(ProxyChoice value)
    {
        OnPropertyChanged(nameof(ProxyIsNone));
        OnPropertyChanged(nameof(ProxyIsSaved));
        OnPropertyChanged(nameof(ProxyIsNew));
        ProxyError = null;
        OnPropertyChanged(nameof(IsSavedProxy));
        OnPropertyChanged(nameof(IsNewProxy));
    }

    /// <summary>Jump hosts in connection order (first hop first).</summary>
    public ObservableCollection<HostOption> JumpChain { get; } = [];
    public ObservableCollection<HostOption> AvailableJumpHosts { get; } = [];
    [ObservableProperty] public partial HostOption? JumpHostToAdd { get; set; }

    // ---- AI / MCP (design §6.3; default Off)
    [ObservableProperty] public partial AiPolicy AiPolicy { get; set; } = AiPolicy.Off;
    public IReadOnlyList<AiPolicy> AiPolicies { get; } = [AiPolicy.Off, AiPolicy.ReadOnly, AiPolicy.Ask, AiPolicy.Trusted];

    // ---- validation (null = valid). Shown via carbon:Field.InvalidText.
    [ObservableProperty] public partial string? LabelError { get; set; }
    [ObservableProperty] public partial string? HostnameError { get; set; }
    [ObservableProperty] public partial string? PortError { get; set; }
    [ObservableProperty] public partial string? AuthError { get; set; }
    [ObservableProperty] public partial string? ProxyError { get; set; }
    [ObservableProperty] public partial string? JumpError { get; set; }
    [ObservableProperty] public partial string? SaveError { get; set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var hosts = await _hosts.ListAsync(ct);
        _allHosts = hosts.ToDictionary(h => h.Id);
        IdentityOptions.Clear();
        foreach (var i in (await _identities.ListAsync(ct)).OrderBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase))
            IdentityOptions.Add(new IdentityOption(i.Id, $"{i.Label}{(i.Username is { Length: > 0 } u ? $" ({u})" : "")}", i.Kind));
        ProxyOptions.Clear();
        foreach (var p in (await _proxies.ListAsync(ct)).OrderBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase))
            ProxyOptions.Add(new ProxyOption(p.Id, $"{p.Label} · {(p.Kind == ProxyKind.Socks5 ? "SOCKS5" : "HTTP")} {p.Hostname}:{p.Port}"));

        if (_original is { } h)
        {
            Label = h.Label;
            Hostname = h.Hostname;
            Port = h.Port.ToString(CultureInfo.InvariantCulture);
            Username = h.Username ?? "";
            Tags = string.Join(", ", h.Tags);
            Notes = h.Notes ?? "";
            AiPolicy = h.AiPolicy;
            SelectedIdentity = IdentityOptions.FirstOrDefault(o => o.Id == h.IdentityId);
            Auth = SelectedIdentity is null ? AuthChoice.NewPassword : AuthChoice.SavedIdentity;
            SelectedProxy = ProxyOptions.FirstOrDefault(o => o.Id == h.ProxyId);
            Proxy = SelectedProxy is null ? ProxyChoice.None : ProxyChoice.Saved;
            JumpChain.Clear();
            foreach (var j in h.JumpChain)
                if (_allHosts.TryGetValue(j, out var jh)) JumpChain.Add(ToOption(jh));
        }
        else if (IdentityOptions.Count > 0)
        {
            Auth = AuthChoice.SavedIdentity;
            SelectedIdentity = IdentityOptions[0];
        }
        RefreshAvailableJumpHosts();
    }

    static HostOption ToOption(Host h) => new(h.Id, h.Label, h.Port == 22 ? h.Hostname : $"{h.Hostname}:{h.Port}");

    void RefreshAvailableJumpHosts()
    {
        AvailableJumpHosts.Clear();
        foreach (var h in _allHosts.Values.Where(h => h.Id != HostId && JumpChain.All(j => j.Id != h.Id))
                     .OrderBy(h => h.Label, StringComparer.CurrentCultureIgnoreCase))
            AvailableJumpHosts.Add(ToOption(h));
    }

    [RelayCommand]
    void AddJump()
    {
        if (JumpHostToAdd is not { } h) return;
        JumpChain.Add(h);
        JumpHostToAdd = null;
        RefreshAvailableJumpHosts();
        JumpError = null;
    }

    [RelayCommand]
    void RemoveJump(HostOption h)
    {
        JumpChain.Remove(h);
        RefreshAvailableJumpHosts();
        JumpError = null;
    }

    [RelayCommand]
    void MoveJumpUp(HostOption h)
    {
        var i = JumpChain.IndexOf(h);
        if (i > 0) JumpChain.Move(i, i - 1);
    }

    [RelayCommand]
    void MoveJumpDown(HostOption h)
    {
        var i = JumpChain.IndexOf(h);
        if (i >= 0 && i < JumpChain.Count - 1) JumpChain.Move(i, i + 1);
    }

    public bool Validate()
    {
        var loc = Loc.Instance;
        LabelError = string.IsNullOrWhiteSpace(Label) ? loc["validation.required"] : null;
        HostnameError = string.IsNullOrWhiteSpace(Hostname) ? loc["validation.required"]
            : Hostname.Trim().Any(char.IsWhiteSpace) ? loc["validation.hostname"] : null;
        PortError = int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536
            ? null : loc["validation.port"];

        AuthError = Auth switch
        {
            AuthChoice.SavedIdentity when SelectedIdentity is null => loc["validation.identity"],
            AuthChoice.NewPassword when Password.Length == 0 => loc["serverForm.passwordRequired"],
            AuthChoice.NewPrivateKey when string.IsNullOrWhiteSpace(PrivateKey) => loc["serverForm.privateKeyRequired"],
            AuthChoice.NewPrivateKey when !PrivateKey.Contains("PRIVATE KEY", StringComparison.Ordinal) => loc["validation.privateKey"],
            _ => null,
        };

        ProxyError = Proxy switch
        {
            ProxyChoice.Saved when SelectedProxy is null => loc["validation.required"],
            ProxyChoice.New when string.IsNullOrWhiteSpace(NewProxyHost) => loc["validation.required"],
            ProxyChoice.New when !(int.TryParse(NewProxyPort, NumberStyles.None, CultureInfo.InvariantCulture, out var pp) && pp is > 0 and < 65536) => loc["validation.port"],
            _ => null,
        };

        JumpError = FindJumpCycle() is { } cycle ? loc.Format("validation.jumpCycle", ("path", cycle)) : null;
        return LabelError is null && HostnameError is null && PortError is null && AuthError is null && ProxyError is null && JumpError is null;
    }

    /// <summary>A jump host must not (transitively) route through the host being edited.</summary>
    string? FindJumpCycle()
    {
        foreach (var hop in JumpChain)
        {
            var path = new List<string> { Label.Trim().Length > 0 ? Label.Trim() : "?", hop.Label };
            if (Reaches(hop.Id, HostId, path, new HashSet<Guid>())) return string.Join(" → ", path);
        }
        return null;
    }

    bool Reaches(Guid from, Guid target, List<string> path, HashSet<Guid> seen)
    {
        if (from == target) return true;
        if (!seen.Add(from) || !_allHosts.TryGetValue(from, out var h)) return false;
        foreach (var next in h.JumpChain)
        {
            path.Add(_allHosts.TryGetValue(next, out var n) ? n.Label : "?");
            if (Reaches(next, target, path, seen)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    /// <summary>Validates and persists. Returns the saved host, or null when invalid / failed (errors are set).</summary>
    public async Task<Host?> SaveAsync(CancellationToken ct = default)
    {
        SaveError = null;
        if (!Validate()) return null;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var label = Label.Trim();
            var user = Username.Trim();

            Guid identityId;
            if (Auth == AuthChoice.SavedIdentity)
            {
                identityId = SelectedIdentity!.Id;
            }
            else
            {
                var isKey = Auth == AuthChoice.NewPrivateKey;
                var identity = new Identity
                {
                    Label = $"{label} · {(isKey ? Loc.Instance["auth.key"] : Loc.Instance["auth.password"])}",
                    Username = user.Length > 0 ? user : null,
                    Kind = isKey ? IdentityKind.PrivateKey : IdentityKind.Password,
                    HasPassphrase = isKey && Passphrase.Length > 0,
                };
                await _identities.SaveAsync(identity, isKey
                    ? new IdentitySecrets(null, NormalizeKey(PrivateKey), Passphrase.Length > 0 ? Passphrase : null)
                    : new IdentitySecrets(Password, null, null), ct);
                identityId = identity.Id;
            }

            Guid? proxyId = Proxy switch
            {
                ProxyChoice.Saved => SelectedProxy!.Id,
                ProxyChoice.New => await CreateProxyAsync(label, ct),
                _ => null,
            };

            var host = (_original ?? new Host { Id = _newId, Label = label, Hostname = Hostname.Trim(), CreatedAt = now }) with
            {
                Label = label,
                Hostname = Hostname.Trim(),
                Port = int.Parse(Port, CultureInfo.InvariantCulture),
                Username = user.Length > 0 ? user : null,
                IdentityId = identityId,
                ProxyId = proxyId,
                JumpChain = [.. JumpChain.Select(j => j.Id)],
                AiPolicy = AiPolicy,
                Tags = [.. Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)],
                Notes = Notes.Trim().Length > 0 ? Notes.Trim() : null,
                UpdatedAt = now,
            };
            await _hosts.SaveAsync(host, ct);
            Password = Passphrase = PrivateKey = NewProxyPassword = ""; // do not keep secrets in the view model
            return host;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException)
        {
            SaveError = Loc.Instance.Format("serverForm.saveFailed", ("error", e.Message));
            return null;
        }
    }

    async Task<Guid> CreateProxyAsync(string hostLabel, CancellationToken ct)
    {
        var proxy = new ProxyProfile
        {
            Label = hostLabel,
            Kind = NewProxyKind,
            Hostname = NewProxyHost.Trim(),
            Port = int.Parse(NewProxyPort, CultureInfo.InvariantCulture),
            Username = NewProxyUsername.Trim().Length > 0 ? NewProxyUsername.Trim() : null,
            HasPassword = NewProxyPassword.Length > 0,
        };
        await _proxies.SaveAsync(proxy, NewProxyPassword.Length > 0 ? NewProxyPassword : null, ct);
        return proxy.Id;
    }

    /// <summary>Pasted keys often lose the trailing newline or use CRLF; OpenSSH parsers are picky.</summary>
    internal static string NormalizeKey(string key) => key.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n";
}
