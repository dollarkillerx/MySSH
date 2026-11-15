using MySSH.Desktop.Localization;

namespace MySSH.Desktop.Tests;

[Collection("Loc")]
public sealed class LocTests
{
    [Theory]
    [InlineData("en-US", "Hosts", "Delete \"web\"?")]
    [InlineData("zh-CN", "主机", "确定删除「web」？")]
    [InlineData("ja-JP", "ホスト", null)]
    public void Lookup_and_format_per_culture(string culture, string hosts, string? deleteConfirm)
    {
        Loc.Instance.SetCulture(culture);
        Assert.Equal(hosts, Loc.Instance["nav.hosts"]);
        if (deleteConfirm is not null)
            Assert.Equal(deleteConfirm, Loc.Instance.Format("servers.deleteConfirm", ("name", "web")));
        Loc.Instance.SetCulture("en-US");
    }

    [Fact]
    public void Missing_key_falls_back_to_key() => Assert.Equal("no.such.key", Loc.Instance["no.such.key"]);

    [Fact]
    public void Bound_observable_republishes_on_culture_change()
    {
        Loc.Instance.SetCulture("en-US");
        var seen = new List<string>();
        var obs = new BindingProbe("nav.settings", seen);
        Loc.Instance.SetCulture("ja-JP");
        Loc.Instance.SetCulture("en-US");
        GC.KeepAlive(obs);
        Assert.Equal(["Settings", "設定", "Settings"], seen);
    }

    sealed class BindingProbe
    {
#pragma warning disable IDE0052 // kept alive on purpose
        readonly IDisposable _sub;
#pragma warning restore IDE0052
        public BindingProbe(string key, List<string> sink) =>
            _sub = Loc.Instance.Observe(key).Subscribe(new Observer(sink));
        sealed class Observer(List<string> sink) : IObserver<string>
        {
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(string value) => sink.Add(value);
        }
    }
}
