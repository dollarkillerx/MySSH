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

    /// <summary>SSH or Windows remote desktop (RDP via the system client; jump host = the SSH host that forwards).</summary>
    [ObservableProperty] public partial HostProtocol Protocol { get; set; } = HostProtocol.Ssh;
    public bool IsRdp => Protocol == HostProtocol.Rdp;
    public bool IsSsh => !IsRdp;
    public bool ProtocolIsSsh { get => !IsRdp; set { if (value) Protocol = HostProtocol.Ssh; } }
    public bool ProtocolIsRdp { get => IsRdp; set { if (value) Protocol = HostProtocol.Rdp; } }
    partial void OnProtocolChanged(HostProtocol value)
    {
        // follow the default port unless the user typed another one
        if (value == HostProtocol.Rdp && Port == "22") Port = Host.DefaultRdpPort.ToString(CultureInfo.InvariantCulture);
        else if (value == HostProtocol.Ssh && Port == Host.DefaultRdpPort.ToString(CultureInfo.InvariantCulture)) Port = "22";
        if (value == HostProtocol.Rdp && Auth == AuthChoice.NewPrivateKey) Auth = AuthChoice.NewPassword; // RDP logs in with a password
        foreach (var name in new[] { nameof(IsRdp), nameof(IsSsh), nameof(ProtocolIsSsh), nameof(ProtocolIsRdp), nameof(HasJumpCandidates), nameof(JumpHelp) })
            OnPropertyChanged(name);
        AddJumpCommand.NotifyCanExecuteChanged();
    }

    /// <summary>RDP: one SSH host forwards (its own jump chain applies); SSH: an ordered chain.</summary>
    public string JumpHelp => Loc.Instance[IsRdp ? "hostEditor.rdpJumpHelp" : "hostEditor.jumpHelp"];
    [ObservableProperty] public partial string Username { get; set; } = "";
    [ObservableProperty] public partial string Tags { get; set; } = "";
    [ObservableProperty] public partial string Notes { get; set; } = "";

    // ---- authentication
    public ObservableCollection<IdentityOption> IdentityOptions { get; } = [];
    [ObservableProperty] public partial AuthChoice Auth { get; set; } = AuthChoice.NewPassword;
    [ObservableProperty] public partial IdentityOption? SelectedIdentity { get; set; }
    /// <summary>The selected saved identity's password, read-only and masked (edited on the Identities page).</summary>
    [ObservableProperty] public partial string SavedIdentityPassword { get; set; } = "";
    public bool ShowSavedIdentityPassword => IsSavedIdentity && SavedIdentityPassword.Length > 0;
    partial void OnSavedIdentityPasswordChanged(string value) => OnPropertyChanged(nameof(ShowSavedIdentityPassword));
    int _secretLoad;

    partial void OnSelectedIdentityChanged(IdentityOption? value) => _ = LoadSavedIdentityPasswordAsync(value);

    async Task LoadSavedIdentityPasswordAsync(IdentityOption? option)
    {
        var load = ++_secretLoad;
        var password = "";
        if (option is { Kind: IdentityKind.Password })
        {
            try { password = (await _identities.GetSecretsAsync(option.Id))?.Password ?? ""; }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or InvalidOperationException) { }
        }
        if (load == _secretLoad) SavedIdentityPassword = password; // ignore results of an older selection
    }
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
        OnPropertyChanged(nameof(ShowSavedIdentityPassword));
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
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AddJumpCommand))] public partial HostOption? JumpHostToAdd { get; set; }
    /// <summary>False when there is no other host to use as a jump host (the picker explains why).</summary>
    public bool HasJumpCandidates => AvailableJumpHosts.Count > 0 && !(IsRdp && JumpChain.Count > 0);
    bool CanAddJump() => JumpHostToAdd is not null && !(IsRdp && JumpChain.Count > 0);

    // ---- AI / MCP (design §6.3; default Off)
    [ObservableProperty] public partial AiPolicy AiPolicy { get; set; } = AiPolicy.Off;
    public IReadOnlyList<AiPolicy> AiPolicies { get; } = [AiPolicy.Off, AiPolicy.ReadOnly, AiPolicy.Ask, AiPolicy.Trusted, AiPolicy.FullAccess];
    public bool IsAiEnabled => AiPolicy != AiPolicy.Off;
    public bool IsAiReadOnly => AiPolicy == AiPolicy.ReadOnly;
    public bool IsAiFullAccess => AiPolicy == AiPolicy.FullAccess;
    partial void OnAiPolicyChanged(AiPolicy value)
    {
        OnPropertyChanged(nameof(IsAiEnabled));
        OnPropertyChanged(nameof(IsAiReadOnly));
        OnPropertyChanged(nameof(IsAiFullAccess));
    }

    /// <summary>First entry (Guid.Empty) = use the host's connection identity.</summary>
    public ObservableCollection<IdentityOption> AiIdentityOptions { get; } = [];
    [ObservableProperty] public partial IdentityOption? SelectedAiIdentity { get; set; }
    /// <summary>ReadOnly allowlist, one command prefix per line; empty = built-in read-only defaults.</summary>
    [ObservableProperty] public partial string AiAllowlist { get; set; } = "";

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
        AiIdentityOptions.Clear();
        AiIdentityOptions.Add(new IdentityOption(Guid.Empty, Loc.Instance["hostEditor.aiIdentityDefault"], IdentityKind.Password));
        foreach (var o in IdentityOptions) AiIdentityOptions.Add(o);
        SelectedAiIdentity = AiIdentityOptions[0];
        ProxyOptions.Clear();
        foreach (var p in (await _proxies.ListAsync(ct)).OrderBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase))
            ProxyOptions.Add(new ProxyOption(p.Id, $"{p.Label} · {(p.Kind == ProxyKind.Socks5 ? "SOCKS5" : "HTTP")} {p.Hostname}:{p.Port}"));

        if (_original is { } h)
        {
            Label = h.Label;
            Hostname = h.Hostname;
            Port = h.Port.ToString(CultureInfo.InvariantCulture);
            Protocol = h.Protocol;
            Username = h.Username ?? "";
            Tags = string.Join(", ", h.Tags);
            Notes = h.Notes ?? "";
            AiPolicy = h.AiPolicy;
            SelectedAiIdentity = AiIdentityOptions.FirstOrDefault(o => o.Id == h.AiIdentityId) ?? AiIdentityOptions[0];
            AiAllowlist = string.Join('\n', h.AiCommandAllowlist);
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
        foreach (var h in _allHosts.Values.Where(h => h.Id != HostId && h.Protocol == HostProtocol.Ssh && JumpChain.All(j => j.Id != h.Id))
                     .OrderBy(h => h.Label, StringComparer.CurrentCultureIgnoreCase))
            AvailableJumpHosts.Add(ToOption(h));
        OnPropertyChanged(nameof(HasJumpCandidates));
    }

    [RelayCommand(CanExecute = nameof(CanAddJump))]
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
            AuthChoice.NewPrivateKey when IsRdp => loc["hostEditor.rdpNeedsPassword"],
            AuthChoice.SavedIdentity when IsRdp && SelectedIdentity is { Kind: IdentityKind.PrivateKey } => loc["hostEditor.rdpNeedsPassword"],
            AuthChoice.SavedIdentity when SelectedIdentity is null => loc["validation.identity"],
            AuthChoice.NewPassword when Password.Length == 0 => loc["serverForm.passwordRequired"],
            AuthChoice.NewPrivateKey when string.IsNullOrWhiteSpace(PrivateKey) => loc["serverForm.privateKeyRequired"],
            AuthChoice.NewPrivateKey when !PrivateKey.Contains("PRIVATE KEY", StringComparison.Ordinal) => loc["validation.privateKey"],
            _ => null,
        };

        ProxyError = IsRdp ? null : Proxy switch
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
        Guid? createdIdentity = null, createdProxy = null;
        // Secrets of an identity / proxy this host already owns are replaced only after the host saved: if the host save
        // fails, the old password / key must still be there.
        var afterHostSaved = new List<Func<Task>>();
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
                // "New password / key" on a host whose identity nobody else uses replaces that identity's secret in
                // place; creating a fresh one each time left same-named orphans behind (used by 0 hosts).
                var isKey = Auth == AuthChoice.NewPrivateKey;
                var reuse = await ExclusiveIdentityAsync(ct);
                var kindLabel = isKey ? Loc.Instance["auth.key"] : Loc.Instance["auth.password"];
                var autoLabel = $"{label} · {kindLabel}";
                // Auto-named identities ("<host> · <kind>") follow host renames / kind changes; user-chosen names stay.
                var keepName = reuse is not null && !(_original is { } o && reuse.Label.StartsWith(o.Label + " · ", StringComparison.Ordinal));
                var identity = (reuse ?? new Identity { Label = autoLabel }) with
                {
                    Label = keepName ? reuse!.Label : await UniqueIdentityLabelAsync(autoLabel, reuse?.Id, ct),
                    Username = user.Length > 0 ? user : null,
                    Kind = isKey ? IdentityKind.PrivateKey : IdentityKind.Password,
                    HasPassphrase = isKey && Passphrase.Length > 0,
                    UpdatedAt = now,
                };
                var secrets = isKey
                    ? new IdentitySecrets(null, NormalizeKey(PrivateKey), Passphrase.Length > 0 ? Passphrase : null)
                    : new IdentitySecrets(Password, null, null);
                if (reuse is null)
                {
                    await _identities.SaveAsync(identity, secrets, ct);
                    createdIdentity = identity.Id;
                }
                else afterHostSaved.Add(() => _identities.SaveAsync(identity, secrets, ct));
                identityId = identity.Id;
            }

            Guid? proxyId = null;
            if (IsRdp) { } // RDP: no proxy (an SSH jump host does the forwarding)
            else if (Proxy == ProxyChoice.Saved) proxyId = SelectedProxy!.Id;
            else if (Proxy == ProxyChoice.New)
            {
                var (id, created, save) = await PrepareNewProxyAsync(label);
                if (created)
                {
                    await save(ct);
                    createdProxy = id;
                }
                else afterHostSaved.Add(() => save(ct));
                proxyId = id;
            }

            var host = (_original ?? new Host { Id = _newId, Label = label, Hostname = Hostname.Trim(), CreatedAt = now }) with
            {
                Label = label,
                Hostname = Hostname.Trim(),
                Port = int.Parse(Port, CultureInfo.InvariantCulture),
                Protocol = Protocol,
                Username = user.Length > 0 ? user : null,
                IdentityId = identityId,
                ProxyId = proxyId,
                JumpChain = [.. JumpChain.Select(j => j.Id)],
                AiPolicy = IsRdp ? AiPolicy.Off : AiPolicy, // AI tools are SSH-only
                AiIdentityId = !IsRdp && SelectedAiIdentity is { } ai && ai.Id != Guid.Empty ? ai.Id : null,
                AiCommandAllowlist = [.. AiAllowlist.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)],
                Tags = [.. Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)],
                Notes = Notes.Trim().Length > 0 ? Notes.Trim() : null,
                UpdatedAt = now,
            };
            await _hosts.SaveAsync(host, ct);
            createdIdentity = createdProxy = null; // committed
            foreach (var save in afterHostSaved) await save();
            Password = Passphrase = PrivateKey = NewProxyPassword = SavedIdentityPassword = ""; // do not keep secrets in the view model
            return host;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException)
        {
            SaveError = Loc.Instance.Format("serverForm.saveFailed", ("error", e.Message));
            return null;
        }
        finally
        {
            // The host was not saved: do not leave the identity / proxy created for it behind.
            if (createdIdentity is { } ci) await TryAsync(() => _identities.DeleteAsync(ci, CancellationToken.None));
            if (createdProxy is { } cp) await TryAsync(() => _proxies.DeleteAsync(cp, CancellationToken.None));
        }
    }

    static async Task TryAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidOperationException or IOException) { }
    }

    /// <summary>True when no host other than this one references <paramref name="id"/> as identity or AI identity.</summary>
    bool OnlyThisHostUsesIdentity(Guid id) =>
        _allHosts.Values.All(h => h.Id == HostId || (h.IdentityId != id && h.AiIdentityId != id));

    /// <summary>The identity this host already uses, if no other host shares it (then "new password" edits it in place).</summary>
    async Task<Identity?> ExclusiveIdentityAsync(CancellationToken ct) =>
        _original?.IdentityId is { } id && OnlyThisHostUsesIdentity(id) && _original.AiIdentityId != id
            ? await _identities.GetAsync(id, ct)
            : null;

    async Task<string> UniqueIdentityLabelAsync(string wanted, Guid? self, CancellationToken ct)
    {
        var taken = (await _identities.ListAsync(ct)).Where(i => i.Id != self).Select(i => i.Label).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var label = wanted;
        for (var n = 2; taken.Contains(label); n++) label = $"{wanted} ({n.ToString(CultureInfo.InvariantCulture)})";
        return label;
    }

    /// <summary>A new proxy, or the host's own proxy updated in place when no other host uses it (saved by the caller).</summary>
    async Task<(Guid Id, bool Created, Func<CancellationToken, Task> Save)> PrepareNewProxyAsync(string hostLabel)
    {
        var reuse = _original?.ProxyId is { } pid && _allHosts.Values.All(h => h.Id == HostId || h.ProxyId != pid)
            ? await _proxies.GetAsync(pid) : null;
        var proxy = (reuse ?? new ProxyProfile { Label = hostLabel, Hostname = "", Port = 0 }) with
        {
            Kind = NewProxyKind,
            Hostname = NewProxyHost.Trim(),
            Port = int.Parse(NewProxyPort, CultureInfo.InvariantCulture),
            Username = NewProxyUsername.Trim().Length > 0 ? NewProxyUsername.Trim() : null,
            HasPassword = NewProxyPassword.Length > 0,
        };
        var password = NewProxyPassword.Length > 0 ? NewProxyPassword : null;
        return (proxy.Id, reuse is null, ct => _proxies.SaveAsync(proxy, password, ct));
    }

    /// <summary>Pasted keys often lose the trailing newline or use CRLF; OpenSSH parsers are picky.</summary>
    internal static string NormalizeKey(string key) => key.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n";
}
