using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;

namespace MySSH.Desktop.ViewModels;

public sealed record IdentityRowViewModel(Guid Id, string Label, string Username, string KindText, int UsedBy, string UsedByText);

/// <summary>"Keys &amp; identities" page: reusable credentials shared by hosts (design §3).</summary>
public sealed partial class IdentitiesViewModel(IIdentityRepository identities, IHostRepository hosts, IUiServices ui) : ObservableObject
{
    public ObservableCollection<IdentityRowViewModel> Rows { get; } = [];
    [ObservableProperty] public partial IdentityRowViewModel? SelectedRow { get; set; }
    [ObservableProperty] public partial IdentityEditorViewModel? Editor { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var all = await identities.ListAsync(ct);
        var allHosts = await hosts.ListAsync(ct);
        Rows.Clear();
        foreach (var i in all.OrderBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            var used = allHosts.Count(h => h.IdentityId == i.Id || h.AiIdentityId == i.Id);
            Rows.Add(new IdentityRowViewModel(i.Id, i.Label, i.Username ?? "",
                i.Kind == IdentityKind.PrivateKey ? Loc.Instance["auth.key"] : Loc.Instance["auth.password"],
                used, Loc.Instance.Format("identities.usedBy", ("count", used))));
        }
    }

    [RelayCommand]
    async Task NewAsync()
    {
        Editor = new IdentityEditorViewModel(identities, ui, null);
        await Editor.LoadAsync();
    }

    [RelayCommand]
    async Task EditAsync(IdentityRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null || await identities.GetAsync(row.Id) is not { } identity) return;
        Editor = new IdentityEditorViewModel(identities, ui, identity);
        await Editor.LoadAsync();
    }

    [RelayCommand]
    async Task SaveEditorAsync()
    {
        if (Editor is null || await Editor.SaveAsync() is null) return;
        Editor = null;
        await LoadAsync();
    }

    [RelayCommand]
    void CancelEditor()
    {
        Editor?.ClearSecrets();
        Editor = null;
    }

    [RelayCommand]
    async Task DeleteAsync(IdentityRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        Error = null;
        if (!await ui.ConfirmAsync(Loc.Instance["common.delete"], Loc.Instance.Format("servers.deleteConfirm", ("name", row.Label)), Loc.Instance["common.delete"], danger: true))
            return;
        try
        {
            await identities.DeleteAsync(row.Id);
            await LoadAsync();
        }
        catch (EntityInUseException)
        {
            Error = Loc.Instance.Format("identities.inUse", ("name", row.Label), ("count", row.UsedBy));
        }
    }
}

/// <summary>Create / edit one identity. Secrets are only written when the user typed new ones.</summary>
public sealed partial class IdentityEditorViewModel(IIdentityRepository identities, IUiServices ui, Identity? existing) : ObservableObject
{
    public bool IsNew => existing is null;
    public string Heading => IsNew ? Loc.Instance["identities.add"] : Loc.Instance["identities.edit"];

    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial string Username { get; set; } = "";
    [ObservableProperty] public partial bool IsPrivateKey { get; set; }
    /// <summary>Edits: prefilled with the stored password (shown masked; the field's eye button reveals it).</summary>
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string PrivateKey { get; set; } = "";
    [ObservableProperty] public partial string Passphrase { get; set; } = "";
    [ObservableProperty] public partial string? LabelError { get; set; }
    [ObservableProperty] public partial string? SecretError { get; set; }
    /// <summary>A stored private key exists but is not shown yet (multi-line plain text: revealed only on request).</summary>
    [ObservableProperty] public partial bool HasHiddenStoredKey { get; set; }
    /// <summary>The stored secret could not be decrypted (the record is kept, see the undecryptable-records notice).</summary>
    [ObservableProperty] public partial string? StoredSecretError { get; set; }
    IdentitySecrets? _stored;

    public string SecretHint => IsNew || !IsPrivateKey ? "" : Loc.Instance["identities.keepKey"];
    partial void OnIsPrivateKeyChanged(bool value) => OnPropertyChanged(nameof(SecretHint));

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (existing is null) return;
        Label = existing.Label;
        Username = existing.Username ?? "";
        IsPrivateKey = existing.Kind == IdentityKind.PrivateKey;
        try
        {
            _stored = await identities.GetSecretsAsync(existing.Id, ct);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            _stored = null;
        }
        if (_stored is null)
        {
            StoredSecretError = Loc.Instance["identities.secretUnreadable"];
            return;
        }
        if (IsPrivateKey)
        {
            Passphrase = _stored.Passphrase ?? "";
            HasHiddenStoredKey = !string.IsNullOrEmpty(_stored.PrivateKey);
        }
        else
        {
            Password = _stored.Password ?? "";
        }
    }

    [RelayCommand]
    void ShowStoredKey()
    {
        PrivateKey = _stored?.PrivateKey ?? "";
        HasHiddenStoredKey = false;
    }

    /// <summary>Called when the editor closes without saving: do not keep secrets around in memory.</summary>
    public void ClearSecrets()
    {
        Password = PrivateKey = Passphrase = "";
        _stored = null;
    }

    [RelayCommand]
    async Task LoadKeyFileAsync()
    {
        var path = await ui.PickOpenFileAsync(Loc.Instance["serverForm.privateKeyHint"]);
        if (path is null) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024) { SecretError = Loc.Instance["validation.privateKey"]; return; }
            PrivateKey = await File.ReadAllTextAsync(path);
            SecretError = null;
        }
        catch (IOException e) { SecretError = e.Message; }
        catch (UnauthorizedAccessException e) { SecretError = e.Message; }
    }

    public async Task<Identity?> SaveAsync(CancellationToken ct = default)
    {
        LabelError = string.IsNullOrWhiteSpace(Label) ? Loc.Instance["validation.required"] : null;
        var kindChanged = existing is not null && (existing.Kind == IdentityKind.PrivateKey) != IsPrivateKey;
        // An unchanged edit of a key identity leaves the key field empty (hidden): reuse the stored key, so a changed
        // passphrase alone is saved too.
        var key = PrivateKey.Trim().Length > 0 ? PrivateKey : !kindChanged ? _stored?.PrivateKey ?? "" : "";
        var newSecretGiven = IsPrivateKey ? key.Trim().Length > 0 : Password.Length > 0;
        SecretError = (IsNew || kindChanged) && !newSecretGiven
            ? Loc.Instance[IsPrivateKey ? "serverForm.privateKeyRequired" : "serverForm.passwordRequired"]
            : IsPrivateKey && newSecretGiven && !key.Contains("PRIVATE KEY", StringComparison.Ordinal) ? Loc.Instance["validation.privateKey"] : null;
        if (LabelError is not null || SecretError is not null) return null;

        var identity = (existing ?? new Identity { Label = Label.Trim() }) with
        {
            Label = Label.Trim(),
            Username = Username.Trim().Length > 0 ? Username.Trim() : null,
            Kind = IsPrivateKey ? IdentityKind.PrivateKey : IdentityKind.Password,
            HasPassphrase = IsPrivateKey && (newSecretGiven ? Passphrase.Length > 0 : existing?.HasPassphrase == true),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        IdentitySecrets? secrets = !newSecretGiven ? null
            : IsPrivateKey ? new IdentitySecrets(null, HostEditorViewModel.NormalizeKey(key), Passphrase.Length > 0 ? Passphrase : null)
            : new IdentitySecrets(Password, null, null);
        await identities.SaveAsync(identity, secrets, ct);
        ClearSecrets();
        return identity;
    }
}
