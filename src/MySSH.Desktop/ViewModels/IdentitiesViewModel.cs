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
    void CancelEditor() => Editor = null;

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
    /// <summary>For edits: leave empty to keep the stored secret.</summary>
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string PrivateKey { get; set; } = "";
    [ObservableProperty] public partial string Passphrase { get; set; } = "";
    [ObservableProperty] public partial string? LabelError { get; set; }
    [ObservableProperty] public partial string? SecretError { get; set; }
    public string SecretHint => IsNew ? "" : Loc.Instance["identities.keepSecret"];

    public Task LoadAsync()
    {
        if (existing is not null)
        {
            Label = existing.Label;
            Username = existing.Username ?? "";
            IsPrivateKey = existing.Kind == IdentityKind.PrivateKey;
        }
        return Task.CompletedTask;
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
        var newSecretGiven = IsPrivateKey ? PrivateKey.Trim().Length > 0 : Password.Length > 0;
        SecretError = (IsNew || kindChanged) && !newSecretGiven
            ? Loc.Instance[IsPrivateKey ? "serverForm.privateKeyRequired" : "serverForm.passwordRequired"]
            : IsPrivateKey && newSecretGiven && !PrivateKey.Contains("PRIVATE KEY", StringComparison.Ordinal) ? Loc.Instance["validation.privateKey"] : null;
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
            : IsPrivateKey ? new IdentitySecrets(null, HostEditorViewModel.NormalizeKey(PrivateKey), Passphrase.Length > 0 ? Passphrase : null)
            : new IdentitySecrets(Password, null, null);
        await identities.SaveAsync(identity, secrets, ct);
        Password = PrivateKey = Passphrase = "";
        return identity;
    }
}
