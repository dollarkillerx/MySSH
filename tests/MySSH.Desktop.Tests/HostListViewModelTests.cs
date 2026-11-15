using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Tests;

[Collection("Loc")] // Loc is a process-wide singleton
public sealed class HostListViewModelTests
{
    static readonly Identity Key = new() { Label = "deploy key", Username = "deploy", Kind = IdentityKind.PrivateKey };
    static readonly Identity Pw = new() { Label = "root pw", Username = "root", Kind = IdentityKind.Password };
    static readonly ProxyProfile Socks = new() { Label = "office", Kind = ProxyKind.Socks5, Hostname = "10.0.0.1", Port = 1080 };
    static readonly Host Bastion = new() { Label = "bastion", Hostname = "jump.example.com", IdentityId = Key.Id, ProxyId = Socks.Id };
    static readonly Host Web = new() { Label = "prod-web-01", Hostname = "10.0.1.12", Port = 2222, IdentityId = Key.Id, JumpChain = [Bastion.Id], AiPolicy = AiPolicy.Ask, Tags = ["prod"] };
    static readonly Host Db = new() { Label = "db", Hostname = "10.0.2.5", Username = "postgres", IdentityId = Pw.Id };

    static HostListViewModel Vm() => new(new InMemoryHosts(Bastion, Web, Db), new InMemoryIdentities(Key, Pw), new InMemoryProxies(Socks));

    [Fact]
    public async Task Rows_resolve_identity_proxy_and_jump_chain()
    {
        Loc.Instance.SetCulture("en-US");
        var vm = Vm();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["bastion", "db", "prod-web-01"], vm.Rows.Select(r => r.Name));
        var web = vm.Rows.Single(r => r.Name == "prod-web-01");
        Assert.Equal("10.0.1.12:2222", web.Address);
        Assert.Equal("deploy", web.User);
        Assert.Equal("Key", web.Auth);
        Assert.Equal("bastion", web.Via);
        Assert.Equal("Ask", web.AiPolicyText);

        var bastion = vm.Rows.Single(r => r.Name == "bastion");
        Assert.Equal("jump.example.com", bastion.Address);
        Assert.Equal("SOCKS5 10.0.0.1:1080", bastion.Via);

        var db = vm.Rows.Single(r => r.Name == "db");
        Assert.Equal("postgres", db.User); // host-level username overrides identity
        Assert.Equal("Password", db.Auth);
        Assert.Equal("Off", db.AiPolicyText);
        Assert.Equal("3 hosts", vm.CountText);
    }

    [Fact]
    public async Task Search_filters_on_name_address_user_via_and_tags()
    {
        Loc.Instance.SetCulture("en-US");
        var vm = Vm();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.SearchText = "prod";
        Assert.Equal(["prod-web-01"], vm.Rows.Select(r => r.Name));
        vm.SearchText = "bastion"; // matches bastion itself and web (via)
        Assert.Equal(["bastion", "prod-web-01"], vm.Rows.Select(r => r.Name));
        vm.SearchText = "POSTGRES";
        Assert.Equal(["db"], vm.Rows.Select(r => r.Name));
        Assert.Equal("1 hosts", vm.CountText);
        vm.SearchText = "";
        Assert.Equal(3, vm.Rows.Count);
    }

    [Fact]
    public async Task Culture_switch_relabels_rows()
    {
        Loc.Instance.SetCulture("en-US");
        var vm = Vm();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Loc.Instance.SetCulture("zh-CN");
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("密钥", vm.Rows.Single(r => r.Name == "prod-web-01").Auth);
        Assert.Equal("3 台主机", vm.CountText);
        Loc.Instance.SetCulture("en-US");
    }
}
