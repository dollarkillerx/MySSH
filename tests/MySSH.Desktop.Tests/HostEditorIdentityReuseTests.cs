using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Tests;

/// <summary>
/// Regression: editing a host and entering a new password again created a second identity with the same name and left the
/// old one used by 0 hosts. Now the host's own identity / proxy is updated in place; shared ones are never touched.
/// </summary>
[Collection("Loc")]
public sealed class HostEditorIdentityReuseTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<Host> SaveNewAsync(TestStore t, string label, string password)
    {
        var ed = new HostEditorViewModel(t.Storage.Hosts, t.Storage.Identities, t.Storage.Proxies, null);
        await ed.LoadAsync(Ct);
        ed.Label = label; ed.Hostname = "203.0.113.10"; ed.Username = "root";
        ed.AuthIsPassword = true; ed.Password = password;
        return (await ed.SaveAsync(Ct))!;
    }

    static async Task<Host> EditWithNewPasswordAsync(TestStore t, Host host, string password, string? rename = null)
    {
        var ed = new HostEditorViewModel(t.Storage.Hosts, t.Storage.Identities, t.Storage.Proxies, host);
        await ed.LoadAsync(Ct);
        if (rename is not null) ed.Label = rename;
        ed.AuthIsPassword = true; ed.Password = password;
        return (await ed.SaveAsync(Ct))!;
    }

    [Fact]
    public async Task New_password_again_updates_the_hosts_own_identity_instead_of_adding_one()
    {
        Loc.Instance.SetCulture("zh-CN");
        await using var t = await TestStore.OpenAsync();
        var ids = t.Storage.Identities;
        var host = await SaveNewAsync(t, "203.0.113.10", "first");
        var original = Assert.Single(await ids.ListAsync(Ct));
        Assert.Equal("203.0.113.10 · 密码", original.Label);

        host = await EditWithNewPasswordAsync(t, host, "second");
        var after = Assert.Single(await ids.ListAsync(Ct)); // no duplicate, no orphan
        Assert.Equal(original.Id, after.Id);
        Assert.Equal(original.Id, host.IdentityId);
        Assert.Equal("second", (await ids.GetSecretsAsync(after.Id, Ct))!.Password);

        host = await EditWithNewPasswordAsync(t, host, "third", rename: "prod-web");
        Assert.Equal("prod-web · 密码", Assert.Single(await ids.ListAsync(Ct)).Label); // auto name follows the host
        Loc.Instance.SetCulture("en-US");
    }

    [Fact]
    public async Task A_shared_identity_is_never_changed_and_new_names_are_unique()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var ids = t.Storage.Identities;
        var a = await SaveNewAsync(t, "web", "shared-pw");
        var shared = Assert.Single(await ids.ListAsync(Ct));
        await t.Storage.Hosts.SaveAsync(new Host { Label = "web-2", Hostname = "10.0.0.2", IdentityId = shared.Id }, Ct);

        await EditWithNewPasswordAsync(t, a, "own-pw");
        var all = await ids.ListAsync(Ct);
        Assert.Equal(2, all.Count);
        Assert.Equal("shared-pw", (await ids.GetSecretsAsync(shared.Id, Ct))!.Password); // web-2 unaffected
        Assert.Equal(all.Count, all.Select(i => i.Label).Distinct().Count());
        Assert.Contains(all, i => i.Label == "web · Password (2)");
    }

    [Fact]
    public async Task New_proxy_again_updates_the_hosts_own_proxy()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var host = await SaveNewAsync(t, "web", "pw");
        for (var port = 1080; port <= 1081; port++)
        {
            var ed = new HostEditorViewModel(t.Storage.Hosts, t.Storage.Identities, t.Storage.Proxies, host);
            await ed.LoadAsync(Ct);
            ed.ProxyIsNew = true; ed.NewProxyHost = "proxy.local"; ed.NewProxyPort = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            host = (await ed.SaveAsync(Ct))!;
        }
        var proxy = Assert.Single(await t.Storage.Proxies.ListAsync(Ct));
        Assert.Equal(1081, proxy.Port);
        Assert.Equal(proxy.Id, host.ProxyId);
    }

    [Fact]
    public async Task Nothing_is_left_behind_when_saving_the_host_fails()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var ed = new HostEditorViewModel(new FailingHosts(t.Storage.Hosts), t.Storage.Identities, t.Storage.Proxies, null);
        await ed.LoadAsync(Ct);
        ed.Label = "web"; ed.Hostname = "10.0.0.5"; ed.AuthIsPassword = true; ed.Password = "pw";
        ed.ProxyIsNew = true; ed.NewProxyHost = "proxy.local"; ed.NewProxyPort = "1080";
        Assert.Null(await ed.SaveAsync(Ct));
        Assert.NotNull(ed.SaveError);
        Assert.Empty(await t.Storage.Identities.ListAsync(Ct));
        Assert.Empty(await t.Storage.Proxies.ListAsync(Ct));
    }

    /// <summary>Review finding: the host's own identity / proxy were overwritten before the host save that then failed.</summary>
    [Fact]
    public async Task A_failed_save_keeps_the_hosts_own_identity_and_proxy_secrets()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var ed = new HostEditorViewModel(t.Storage.Hosts, t.Storage.Identities, t.Storage.Proxies, null);
        await ed.LoadAsync(Ct);
        ed.Label = "web"; ed.Hostname = "10.0.0.5"; ed.AuthIsPassword = true; ed.Password = "old-pw";
        ed.ProxyIsNew = true; ed.NewProxyHost = "proxy.local"; ed.NewProxyPort = "1080"; ed.NewProxyPassword = "old-proxy-pw";
        var host = (await ed.SaveAsync(Ct))!;

        ed = new HostEditorViewModel(new FailingHosts(t.Storage.Hosts), t.Storage.Identities, t.Storage.Proxies, host);
        await ed.LoadAsync(Ct);
        ed.AuthIsPassword = true; ed.Password = "new-pw";
        ed.ProxyIsNew = true; ed.NewProxyHost = "proxy2.local"; ed.NewProxyPort = "1081"; ed.NewProxyPassword = "new-proxy-pw";
        Assert.Null(await ed.SaveAsync(Ct));

        Assert.Equal("old-pw", (await t.Storage.Identities.GetSecretsAsync(host.IdentityId!.Value, Ct))!.Password);
        var proxy = (await t.Storage.Proxies.GetAsync(host.ProxyId!.Value, Ct))!;
        Assert.Equal(("proxy.local", 1080), (proxy.Hostname, proxy.Port));
        Assert.Equal("old-proxy-pw", await t.Storage.Proxies.GetPasswordAsync(proxy.Id, Ct));
    }

    sealed class FailingHosts(IHostRepository inner) : IHostRepository
    {
        public Task<IReadOnlyList<Host>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public Task<Host?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task SaveAsync(Host host, CancellationToken ct = default) => throw new InvalidOperationException("disk full");
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
    }
}
