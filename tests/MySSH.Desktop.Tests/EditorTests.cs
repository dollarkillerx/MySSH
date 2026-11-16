using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Tests;

[Collection("Loc")]
public sealed class EditorTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    const string Key = "-----BEGIN OPENSSH PRIVATE KEY-----\r\nAAAA\r\n-----END OPENSSH PRIVATE KEY-----";

    [Fact]
    public async Task New_host_with_new_password_and_new_proxy_creates_all_three()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var s = t.Storage;
        var ed = new HostEditorViewModel(s.Hosts, s.Identities, s.Proxies, null);
        await ed.LoadAsync(Ct);
        Assert.True(ed.AuthIsPassword); // no identities yet

        ed.Label = "web"; ed.Hostname = "10.0.0.5"; ed.Port = "2222"; ed.Username = "deploy";
        ed.Password = "pw"; ed.ProxyIsNew = true; ed.NewProxyHost = "proxy.local"; ed.NewProxyPort = "1080"; ed.NewProxyPassword = "pp";
        ed.Tags = "prod, web, PROD";
        var host = await ed.SaveAsync(Ct);

        Assert.NotNull(host);
        Assert.Equal(2222, host!.Port);
        Assert.Equal(["prod", "web"], host.Tags);
        Assert.Equal(AiPolicy.Off, host.AiPolicy);
        var identity = Assert.Single(await s.Identities.ListAsync(Ct));
        Assert.Equal("pw", (await s.Identities.GetSecretsAsync(identity.Id, Ct))!.Password);
        var proxy = Assert.Single(await s.Proxies.ListAsync(Ct));
        Assert.Equal("pp", await s.Proxies.GetPasswordAsync(proxy.Id, Ct));
        Assert.Equal(proxy.Id, host.ProxyId);
        Assert.Equal("", ed.Password); // secrets cleared from the view model
    }

    [Fact]
    public async Task Validation_reports_each_field()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var ed = new HostEditorViewModel(t.Storage.Hosts, t.Storage.Identities, t.Storage.Proxies, null);
        await ed.LoadAsync(Ct);
        ed.Hostname = "bad host"; ed.Port = "70000"; ed.AuthIsKey = true; ed.PrivateKey = "not a key";
        Assert.Null(await ed.SaveAsync(Ct));
        Assert.Equal("Required", ed.LabelError);
        Assert.Equal("Host names cannot contain spaces", ed.HostnameError);
        Assert.Equal("Enter a port between 1 and 65535", ed.PortError);
        Assert.StartsWith("Not a private key", ed.AuthError, StringComparison.Ordinal);
        Assert.Empty(await t.Storage.Hosts.ListAsync(Ct));
    }

    [Fact]
    public async Task Jump_chain_cycle_is_rejected()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var s = t.Storage;
        var a = new Host { Label = "A", Hostname = "a" };
        var b = new Host { Label = "B", Hostname = "b", JumpChain = [a.Id] };
        await s.Hosts.SaveAsync(a, Ct);
        await s.Hosts.SaveAsync(b, Ct);

        // editing A to go through B would give A → B → A
        var ed = new HostEditorViewModel(s.Hosts, s.Identities, s.Proxies, a);
        await ed.LoadAsync(Ct);
        Assert.DoesNotContain(ed.AvailableJumpHosts, o => o.Id == a.Id); // never offered itself
        ed.JumpHostToAdd = ed.AvailableJumpHosts.Single(o => o.Id == b.Id);
        ed.AddJumpCommand.Execute(null);
        ed.Password = "x";
        Assert.Null(await ed.SaveAsync(Ct));
        Assert.Equal("Jump chain loops back to this host: A → B → A", ed.JumpError);
    }

    [Fact]
    public async Task Jump_chain_order_can_be_changed()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var s = t.Storage;
        var j1 = new Host { Label = "j1", Hostname = "j1" };
        var j2 = new Host { Label = "j2", Hostname = "j2" };
        await s.Hosts.SaveAsync(j1, Ct);
        await s.Hosts.SaveAsync(j2, Ct);
        var ed = new HostEditorViewModel(s.Hosts, s.Identities, s.Proxies, null);
        await ed.LoadAsync(Ct);
        ed.Label = "target"; ed.Hostname = "t"; ed.Password = "x";
        foreach (var id in new[] { j1.Id, j2.Id }) { ed.JumpHostToAdd = ed.AvailableJumpHosts.Single(o => o.Id == id); ed.AddJumpCommand.Execute(null); }
        ed.MoveJumpUpCommand.Execute(ed.JumpChain[1]);
        var host = await ed.SaveAsync(Ct);
        Assert.Equal([j2.Id, j1.Id], host!.JumpChain);
    }

    [Fact]
    public async Task Deleting_a_jump_host_in_use_shows_who_uses_it()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var s = t.Storage;
        var bastion = new Host { Label = "bastion", Hostname = "b" };
        await s.Hosts.SaveAsync(bastion, Ct);
        await s.Hosts.SaveAsync(new Host { Label = "db", Hostname = "d", JumpChain = [bastion.Id] }, Ct);
        var ui = new FakeUi();
        var list = new HostListViewModel(s.Hosts, s.Identities, s.Proxies, ui);
        await list.LoadAsync(Ct);
        await list.DeleteHostCommand.ExecuteAsync(list.Rows.Single(r => r.Name == "bastion"));
        Assert.Equal("\"bastion\" is a jump host for: db. Remove it from their jump chains first.", list.Error);
        Assert.Equal(2, list.Rows.Count);

        ui.ConfirmResult = false; // cancelled confirm deletes nothing
        await list.DeleteHostCommand.ExecuteAsync(list.Rows.Single(r => r.Name == "db"));
        Assert.Equal(2, (await s.Hosts.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task Identity_edit_keeps_secret_unless_retyped_and_delete_in_use_is_refused()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var s = t.Storage;
        var ui = new FakeUi();
        var page = new IdentitiesViewModel(s.Identities, s.Hosts, ui);
        await page.NewCommand.ExecuteAsync(null);
        page.Editor!.Label = "deploy key"; page.Editor.IsPrivateKey = true; page.Editor.PrivateKey = Key; page.Editor.Passphrase = "pp";
        await page.SaveEditorCommand.ExecuteAsync(null);
        Assert.Null(page.Editor);
        var row = Assert.Single(page.Rows);

        await page.EditCommand.ExecuteAsync(row);
        page.Editor!.Label = "deploy key (renamed)"; // secrets left empty
        await page.SaveEditorCommand.ExecuteAsync(null);
        var secrets = await s.Identities.GetSecretsAsync(row.Id, Ct);
        Assert.Equal("-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n", secrets!.PrivateKey); // normalised, unchanged
        Assert.Equal("pp", secrets.Passphrase);

        await s.Hosts.SaveAsync(new Host { Label = "web", Hostname = "w", IdentityId = row.Id }, Ct);
        await page.LoadAsync(Ct);
        await page.DeleteCommand.ExecuteAsync(page.Rows.Single());
        Assert.Equal("\"deploy key (renamed)\" is used by 1 hosts. Assign them another identity first.", page.Error);
        Assert.Single(await s.Identities.ListAsync(Ct));
    }

    [Fact]
    public async Task Settings_persist_and_apply()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        DesktopSettings? applied = null;
        var vm = new SettingsViewModel(t.Storage, new FakeUi(), p => applied = p);
        await vm.LoadAsync(Ct);
        Assert.Null(applied); // loading does not re-apply
        vm.Theme = ThemePreference.Dark;
        vm.Language = vm.Languages.Single(l => l.Culture == "ja-JP");
        await Task.Delay(50, Ct); // persistence is fire-and-forget
        Assert.Equal("ja-JP", applied!.Language);
        var stored = await DesktopSettings.LoadAsync(t.Storage.Settings, Ct);
        Assert.Equal(ThemePreference.Dark, stored.Theme);
        Assert.Equal("ja-JP", stored.Language);
        Loc.Instance.SetCulture("en-US");
    }

    [Fact]
    public async Task Backup_export_then_import_round_trips_through_the_settings_page()
    {
        Loc.Instance.SetCulture("en-US");
        var file = Path.Combine(Path.GetTempPath(), $"myssh-test-{Guid.NewGuid():N}.mysshbk");
        try
        {
            await using (var a = await TestStore.OpenAsync())
            {
                await a.Storage.Hosts.SaveAsync(new Host { Label = "web", Hostname = "w" }, Ct);
                var vm = new SettingsViewModel(a.Storage, new FakeUi { Password = "short" , SavePath = file }, _ => { });
                await vm.ExportBackupCommand.ExecuteAsync(null);
                Assert.True(vm.MessageIsError); // < 8 chars rejected
                vm = new SettingsViewModel(a.Storage, new FakeUi { Password = "long enough pw", SavePath = file }, _ => { });
                await vm.ExportBackupCommand.ExecuteAsync(null);
                Assert.False(vm.MessageIsError);
                Assert.True(File.Exists(file));
            }
            await using var b = await TestStore.OpenAsync();
            var wrong = new SettingsViewModel(b.Storage, new FakeUi { Password = "nope nope nope", OpenPath = file }, _ => { });
            await wrong.ImportBackupCommand.ExecuteAsync(null);
            Assert.Equal("Wrong password, or the file is damaged.", wrong.Message);
            var right = new SettingsViewModel(b.Storage, new FakeUi { Password = "long enough pw", OpenPath = file }, _ => { });
            var reloaded = false;
            right.BackupImported += () => reloaded = true;
            await right.ImportBackupCommand.ExecuteAsync(null);
            Assert.False(right.MessageIsError);
            Assert.True(reloaded);
            Assert.Equal("web", Assert.Single(await b.Storage.Hosts.ListAsync(Ct)).Label);
        }
        finally { File.Delete(file); }
    }
}
