using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.Tests;
using MySSH.Desktop.ViewModels;
using MySSH.Storage;
using MySSH.UI.Carbon;

[assembly: AvaloniaTestApplication(typeof(HeadlessApp))]

namespace MySSH.Desktop.Tests;

/// <summary>The real App (CarbonTheme from App.axaml) on headless Skia. Its desktop-lifetime startup does not run here.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>P1 acceptance, end to end through the real MainWindow: legacy offer → import → host table. Screenshots in .work/.</summary>
[Collection("Loc")]
public sealed class MainWindowUiTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("myssh-ui-tests-").FullName;

    StorageOptions Options(IOsKeyStore? ks) => new()
    {
        Paths = new AppPaths { DataDirectory = Path.Combine(_root, "MySSH.Desktop"), LegacyDataDirectory = Path.Combine(_root, "legacy", "myssh") },
        OsKeyStore = ks,
        UsePlatformKeyStore = false,
        Argon2 = new Argon2Parameters(1024, 1, 1),
    };

    [AvaloniaFact]
    public async Task Legacy_import_flow_renders_host_table()
    {
        Loc.Instance.SetCulture("en-US");
        var o = Options(new MemKeyStore());
        var src = Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy", "appdata", "myssh");
        Directory.CreateDirectory(o.Paths.LegacyDataDirectory);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(o.Paths.LegacyDataDirectory, Path.GetFileName(f)));

        await using var vm = new ShellViewModel(ct => AppSession.OpenAsync(o, ct));
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        w.Show();
        await vm.InitializeAsync();
        Pump();

        var legacy = w.FindControl<Modal>("LegacyModal")!;
        Assert.True(legacy.IsOpen);
        Save(w, "01-legacy-offer-light");

        await vm.ImportLegacyCommand.ExecuteAsync(null);
        Pump();
        Assert.False(legacy.IsOpen);
        var grid = w.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "HostGrid");
        Assert.Equal(vm.Hosts!.Rows.Count, grid.ItemsSource!.Cast<object>().Count());
        Assert.True(vm.Hosts.Rows.Count > 0);
        Save(w, "02-hosts-light");

        // P2 pages: host editor side panel, identities, known hosts, settings
        await vm.Hosts.NewHostCommand.ExecuteAsync(null);
        Pump();
        Save(w, "06-host-editor-light");
        vm.Hosts.Editor!.Label = "web";
        vm.Hosts.Editor.Hostname = "bad host";
        await vm.Hosts.SaveEditorCommand.ExecuteAsync(null); // invalid → stays open with field errors
        Pump();
        Save(w, "07-host-editor-errors-light");
        vm.Hosts.CancelEditorCommand.Execute(null);
        foreach (var (page, name) in new[] { (1, "08-identities-light"), (2, "09-known-hosts-light"), (4, "10-settings-light") })
        {
            vm.SelectedNav = vm.NavItems[page];
            await Task.Delay(50);
            Pump();
            Save(w, name);
        }
        vm.SelectedNav = vm.NavItems[0];
        await Task.Delay(50);

        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Pump();
        Save(w, "03-hosts-dark");

        Loc.Instance.SetCulture("zh-CN");
        Pump();
        Save(w, "04-hosts-dark-zh");
        Loc.Instance.SetCulture("en-US");
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        w.Close();
    }

    [AvaloniaFact]
    public async Task Master_password_modal_shows_error_and_stays_open()
    {
        Loc.Instance.SetCulture("en-US");
        var o = Options(null);
        await using (var first = new ShellViewModel(ct => AppSession.OpenAsync(o, ct)))
        {
            await first.InitializeAsync();
            first.MasterPassword = "right";
            await first.SubmitMasterPasswordCommand.ExecuteAsync(null);
        }

        await using var vm = new ShellViewModel(ct => AppSession.OpenAsync(o, ct));
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        w.Show();
        await vm.InitializeAsync();
        vm.MasterPassword = "wrong";
        await vm.SubmitMasterPasswordCommand.ExecuteAsync(null);
        Pump();
        var modal = w.FindControl<Modal>("VaultModal")!;
        Assert.True(modal.IsOpen);
        modal.Close(); // Esc / close button must not dismiss a locked vault
        Pump();
        Assert.True(modal.IsOpen);
        Save(w, "05-vault-wrong-password-dark");
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        w.Close();
    }

    static void Pump()
    {
        for (var i = 0; i < 30; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    }

    static void Save(Window w, string name, [CallerFilePath] string here = "")
    {
        Pump();
        var dir = Path.Combine(Path.GetDirectoryName(here)!, ".work", "screenshots");
        Directory.CreateDirectory(dir);
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    sealed class MemKeyStore : IOsKeyStore
    {
        byte[]? _k;
        public string Kind => "test-memory";
        public bool IsAvailable => true;
        public byte[]? Load() => _k?.ToArray();
        public void Save(ReadOnlySpan<byte> key) => _k = key.ToArray();
        public void Delete() => _k = null;
    }
}

[Collection("Loc")]
public sealed class HostKeyPromptUiTests
{
    static readonly HostKeyCheck Changed = new("[db.internal]:2222", "ssh-ed25519", "SHA256:Zq3m6vP0yJ0rC6pY0t0Hq3T5kK0p8b0w7xV1nYcE2aQ", "AAAA", HostKeyStatus.Changed,
        "SHA256:4k9hR8Gqf0Xbq1w5yS7l3cQyQ2n0p1tY6r9e8w7v6u5", "db", HopIndex: 1, HopCount: 2);

    [AvaloniaFact]
    public async Task Changed_key_cannot_be_accepted_without_explicit_verification()
    {
        Loc.Instance.SetCulture("en-US");
        var w = new MainWindow { Width = 1280, Height = 800 };
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        w.Show();
        var decision = w.VerifyAsync(Changed, CancellationToken.None).AsTask();
        MainWindowUiTestsHelpers.Pump();
        var modal = w.FindControl<Modal>("HostKeyModal")!;
        Assert.True(modal.IsOpen);
        Assert.True(modal.IsDanger);
        Assert.False(modal.IsPrimaryButtonEnabled);
        w.FindControl<CheckBox>("HostKeyVerified")!.IsChecked = true;
        MainWindowUiTestsHelpers.Pump();
        Assert.True(modal.IsPrimaryButtonEnabled);
        var primary = modal.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_PrimaryButton");
        Assert.True(primary.IsEffectivelyEnabled, $"IsEnabled={primary.IsEnabled} classes={string.Join(",", primary.Classes)} bg={primary.Background}");
        // Brush transitions run on wall-clock time even in headless mode: let them finish before asserting colours.
        for (var i = 0; i < 10; i++) { await Task.Delay(30); MainWindowUiTestsHelpers.Pump(); }
        Assert.Equal(Avalonia.Media.Color.Parse("#da1e28"), (primary.Background as Avalonia.Media.ISolidColorBrush)?.Color); // Carbon danger
        MainWindowUiTestsHelpers.Save(w, "11-host-key-changed-light");
        modal.Close(ModalResult.Secondary);
        Assert.Equal(HostKeyDecision.Reject, await decision);

        var unknown = Changed with { Status = HostKeyStatus.Unknown, PreviousFingerprint = null, HopIndex = 0, HopCount = 1 };
        decision = w.VerifyAsync(unknown, CancellationToken.None).AsTask();
        MainWindowUiTestsHelpers.Pump();
        Assert.True(modal.IsPrimaryButtonEnabled);
        Assert.False(modal.IsDanger);
        MainWindowUiTestsHelpers.Save(w, "12-host-key-unknown-light");
        modal.Close(ModalResult.Primary);
        Assert.Equal(HostKeyDecision.AcceptAndSave, await decision);
        w.Close();
    }
}

static class MainWindowUiTestsHelpers
{
    public static void Pump()
    {
        for (var i = 0; i < 30; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    }

    public static void Save(Window w, string name, [CallerFilePath] string here = "")
    {
        Pump();
        var dir = Path.Combine(Path.GetDirectoryName(here)!, ".work", "screenshots");
        Directory.CreateDirectory(dir);
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
