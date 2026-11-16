using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.Desktop.Views;

namespace MySSH.Desktop.Tests;

[Collection("Loc")]
public sealed class UpdateTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class FakeUpdater : IAppUpdater
    {
        public bool IsInstalled { get; init; } = true;
        public string CurrentVersion => "0.2.0";
        public string? PendingVersion { get; init; }
        public string? Available { get; set; }
        public Exception? Fail { get; set; }
        public int Applied { get; private set; }

        public Task<string?> CheckAsync(CancellationToken ct) => Fail is { } e ? Task.FromException<string?>(e) : Task.FromResult(Available);

        public Task DownloadAsync(Action<int> progress, CancellationToken ct)
        {
            progress(40);
            progress(100);
            return Task.CompletedTask;
        }

        public void ApplyAfterExit() => Applied++;
    }

    [Fact]
    public void A_development_build_cannot_check()
    {
        Loc.Instance.SetCulture("en-US");
        var vm = new UpdatesViewModel(NullAppUpdater.Instance, () => { });
        Assert.Equal(UpdateState.NotInstalled, vm.State);
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.Contains("development build", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_downloads_then_restart_applies_after_quitting()
    {
        Loc.Instance.SetCulture("en-US");
        var updater = new FakeUpdater { Available = "0.2.1" };
        var quits = 0;
        string? ready = null;
        var vm = new UpdatesViewModel(updater, () => quits++);
        vm.UpdateReady += v => ready = v;
        Assert.Equal("MySSH 0.2.0", vm.CurrentVersion);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Ready, vm.State);
        Assert.Equal("0.2.1", ready);
        Assert.Equal(100, vm.Progress);
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.Contains("0.2.1 is ready", vm.StatusText, StringComparison.Ordinal);
        vm.RestartCommand.Execute(null);
        Assert.Equal(1, updater.Applied);
        Assert.Equal(1, quits);
    }

    [Fact]
    public async Task Up_to_date_and_failures_are_shown_only_for_a_manual_check()
    {
        Loc.Instance.SetCulture("en-US");
        var updater = new FakeUpdater();
        var vm = new UpdatesViewModel(updater, () => { });
        await vm.CheckInBackgroundAsync(Ct);
        Assert.Equal(UpdateState.Idle, vm.State);
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.UpToDate, vm.State);

        updater.Fail = new HttpRequestException("rate limited");
        var quiet = new UpdatesViewModel(updater, () => { });
        await quiet.CheckInBackgroundAsync(Ct);
        Assert.Equal(UpdateState.Idle, quiet.State);
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Equal("Could not check for updates: rate limited", vm.StatusText);
        vm.RestartCommand.Execute(null); // nothing to install
        Assert.Equal(0, updater.Applied);
    }

    [Fact]
    public void An_update_downloaded_by_an_earlier_run_is_ready_at_startup()
    {
        Loc.Instance.SetCulture("en-US");
        var vm = new UpdatesViewModel(new FakeUpdater { PendingVersion = "0.2.1" }, () => { });
        Assert.Equal(UpdateState.Ready, vm.State);
        Assert.Equal("0.2.1", vm.NewVersion);
    }

    [AvaloniaFact]
    public async Task Settings_page_shows_restart_when_an_update_is_ready()
    {
        Loc.Instance.SetCulture("en-US");
        await using var t = await TestStore.OpenAsync();
        var settings = new SettingsViewModel(t.Storage, new FakeUi(), _ => { });
        await settings.LoadAsync(Ct);
        Assert.True(settings.CheckForUpdates);
        settings.Updates = new UpdatesViewModel(new FakeUpdater { Available = "0.2.1" }, () => { });
        var page = new SettingsPage { DataContext = settings };
        var w = new Window { Content = page, Width = 900, Height = 900 };
        w.Show();
        Button Find(string name) => page.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == name);
        Assert.True(Find("CheckUpdates").IsVisible);
        Assert.False(Find("RestartToUpdate").IsVisible);

        await settings.Updates.CheckCommand.ExecuteAsync(null);
        Assert.False(Find("CheckUpdates").IsVisible);
        Assert.True(Find("RestartToUpdate").IsVisible);

        settings.CheckForUpdates = false;
        await Task.Delay(50, Ct); // persistence is fire-and-forget
        Assert.False((await DesktopSettings.LoadAsync(t.Storage.Settings, Ct)).CheckForUpdates);
        w.Close();
    }
}
