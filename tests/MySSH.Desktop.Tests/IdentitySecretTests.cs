using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.ViewModels;
using MySSH.Desktop.Views;

namespace MySSH.Desktop.Tests;

/// <summary>Saved passwords can be viewed: the editor loads them masked; private keys only on request.</summary>
[Collection("Loc")]
public sealed class IdentitySecretTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    const string Key = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n";

    [Fact]
    public async Task Editing_a_password_identity_shows_the_saved_password_and_saves_changes()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var ids = t.Storage.Identities;
        var id = new Identity { Label = "web", Username = "deploy", Kind = IdentityKind.Password };
        await ids.SaveAsync(id, new IdentitySecrets("s3cret-pw", null, null), Ct);

        var ed = new IdentityEditorViewModel(ids, new FakeUi(), id);
        await ed.LoadAsync(Ct);
        Assert.Equal("s3cret-pw", ed.Password);
        Assert.Null(ed.StoredSecretError);
        Assert.Equal("", ed.SecretHint);

        ed.Password = "changed";
        Assert.NotNull(await ed.SaveAsync(Ct));
        Assert.Equal("changed", (await ids.GetSecretsAsync(id.Id, Ct))!.Password);
        Assert.Equal("", ed.Password); // wiped after saving

        ed = new IdentityEditorViewModel(ids, new FakeUi(), id);
        await ed.LoadAsync(Ct);
        ed.Password = ""; // cleared field keeps the stored password
        Assert.NotNull(await ed.SaveAsync(Ct));
        Assert.Equal("changed", (await ids.GetSecretsAsync(id.Id, Ct))!.Password);
    }

    [Fact]
    public async Task Key_identity_keeps_the_key_hidden_until_asked_and_saves_a_new_passphrase()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var ids = t.Storage.Identities;
        var id = new Identity { Label = "key", Kind = IdentityKind.PrivateKey, HasPassphrase = true };
        await ids.SaveAsync(id, new IdentitySecrets(null, Key, "old-pass"), Ct);

        var ed = new IdentityEditorViewModel(ids, new FakeUi(), id);
        await ed.LoadAsync(Ct);
        Assert.Equal("old-pass", ed.Passphrase);
        Assert.Equal("", ed.PrivateKey);
        Assert.True(ed.HasHiddenStoredKey);
        Assert.NotEqual("", ed.SecretHint);

        ed.Passphrase = "new-pass"; // only the passphrase changes; the hidden key is kept
        Assert.NotNull(await ed.SaveAsync(Ct));
        var s = (await ids.GetSecretsAsync(id.Id, Ct))!;
        Assert.Equal("new-pass", s.Passphrase);
        Assert.Contains("OPENSSH PRIVATE KEY", s.PrivateKey, StringComparison.Ordinal);

        ed = new IdentityEditorViewModel(ids, new FakeUi(), id);
        await ed.LoadAsync(Ct);
        ed.ShowStoredKeyCommand.Execute(null);
        Assert.Contains("OPENSSH PRIVATE KEY", ed.PrivateKey, StringComparison.Ordinal);
        Assert.False(ed.HasHiddenStoredKey);
        ed.ClearSecrets();
        Assert.Equal("", ed.PrivateKey);
        Assert.Equal("", ed.Passphrase);
    }

    [Fact]
    public async Task Host_editor_shows_the_saved_identity_password_read_only()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var s = t.Storage;
        var pw = new Identity { Label = "pw", Kind = IdentityKind.Password };
        var key = new Identity { Label = "key", Kind = IdentityKind.PrivateKey };
        await s.Identities.SaveAsync(pw, new IdentitySecrets("host-pw", null, null), Ct);
        await s.Identities.SaveAsync(key, new IdentitySecrets(null, Key, null), Ct);
        var host = new Host { Label = "web", Hostname = "10.0.0.5", IdentityId = pw.Id };
        await s.Hosts.SaveAsync(host, Ct);

        var ed = new HostEditorViewModel(s.Hosts, s.Identities, s.Proxies, host);
        await ed.LoadAsync(Ct);
        for (var i = 0; i < 100 && ed.SavedIdentityPassword.Length == 0; i++) await Task.Delay(10, Ct);
        Assert.Equal("host-pw", ed.SavedIdentityPassword);
        Assert.True(ed.ShowSavedIdentityPassword);

        ed.SelectedIdentity = ed.IdentityOptions.Single(o => o.Id == key.Id); // key identity: nothing to show
        for (var i = 0; i < 100 && ed.SavedIdentityPassword.Length > 0; i++) await Task.Delay(10, Ct);
        Assert.False(ed.ShowSavedIdentityPassword);
        ed.AuthIsPassword = true; // "new password" hides it as well
        Assert.False(ed.ShowSavedIdentityPassword);
    }

    [AvaloniaFact]
    public async Task Password_is_masked_until_revealed()
    {
        Loc.Instance.SetCulture("zh-CN");
        await using var t = await TestStore.OpenAsync();
        var ids = t.Storage.Identities;
        var id = new Identity { Label = "web", Username = "deploy", Kind = IdentityKind.Password };
        await ids.SaveAsync(id, new IdentitySecrets("s3cret-pw", null, null), Ct);
        var vm = new IdentitiesViewModel(ids, t.Storage.Hosts, new FakeUi());
        await vm.LoadAsync(Ct);
        await vm.EditCommand.ExecuteAsync(vm.Rows.Single());
        var page = new IdentitiesPage { DataContext = vm };
        var w = new Window { Content = page, Width = 1100, Height = 700 };
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        w.Show();
        MainWindowUiTestsHelpers.Pump();
        var box = w.GetVisualDescendants().OfType<TextBox>().Single(b => b.Text == "s3cret-pw");
        Assert.Equal('●', box.PasswordChar);
        Assert.False(box.RevealPassword);
        MainWindowUiTestsHelpers.Save(w, "32-identity-password-masked-zh");
        box.RevealPassword = true; // what the field's eye button toggles
        MainWindowUiTestsHelpers.Save(w, "33-identity-password-revealed-zh");
        Loc.Instance.SetCulture("en-US");
        w.Close();
    }
}
