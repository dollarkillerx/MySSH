using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.Storage;

namespace MySSH.Desktop.Tests;

[Collection("Loc")]
/// <summary>The add-hop flow through the real UI: disabled until picked, then adds; no UI exception on the way.</summary>
public sealed class AddHopUiTests
{
    static void Pump() { for (var i = 0; i < 20; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); } }

    static void Click(Window w, Control c)
    {
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)!.Value;
        w.MouseMove(p); w.MouseDown(p, MouseButton.Left); w.MouseUp(p, MouseButton.Left);
        Pump();
    }

    [AvaloniaFact]
    public async Task Picking_a_jump_host_enables_add_hop_and_adds_it()
    {
        Loc.Instance.SetCulture("zh-CN");
        var errors = new List<Exception>();
        Dispatcher.UIThread.UnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
        var root = Directory.CreateTempSubdirectory("hop-").FullName;
        var o = new StorageOptions
        {
            Paths = new AppPaths { DataDirectory = Path.Combine(root, "d"), LegacyDataDirectory = Path.Combine(root, "l") },
            OsKeyStore = new MemKs(), UsePlatformKeyStore = false, Argon2 = new Argon2Parameters(1024, 1, 1),
        };
        await using var vm = new ShellViewModel(ct => AppSession.OpenAsync(o, ct));
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        w.Show();
        await vm.InitializeAsync();
        await vm.Session!.Storage.Hosts.SaveAsync(new Host { Label = "AI", Hostname = "10.0.0.1" });
        await vm.Hosts!.LoadAsync();
        await vm.Hosts.NewHostCommand.ExecuteAsync(null);
        Pump();
        var picker = w.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "JumpHostPicker");
        var add = w.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "AddHopButton");
        Assert.True(picker.IsEffectivelyEnabled);
        Assert.Equal(1, picker.ItemCount);
        Assert.False(add.IsEffectivelyEnabled);
        Click(w, add); // click while disabled
        Click(w, picker); // open the dropdown
        picker.SelectedIndex = 0;
        picker.IsDropDownOpen = false;
        Pump();
        Assert.True(add.IsEffectivelyEnabled);
        add.BringIntoView();
        Pump();
        Click(w, add);
        Assert.Equal("AI", Assert.Single(vm.Hosts.Editor!.JumpChain).Label);
        Assert.False(picker.IsEffectivelyEnabled); // nothing left to pick
        Assert.Empty(errors);
        Loc.Instance.SetCulture("en-US");
        w.Close();
    }

    sealed class MemKs : IOsKeyStore
    {
        byte[]? _k;
        public string Kind => "m";
        public bool IsAvailable => true;
        public byte[]? Load() => _k;
        public void Save(ReadOnlySpan<byte> key) => _k = key.ToArray();
        public void Delete() => _k = null;
    }
}
