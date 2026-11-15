using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.Storage;

namespace MySSH.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new ShellViewModel(ct => AppSession.OpenAsync(StorageOptions.Default, ct));
            var window = new MainWindow { DataContext = shell };
            window.Opened += async (_, _) => await shell.InitializeAsync();
            desktop.ShutdownRequested += (_, _) => shell.DisposeAsync().AsTask().GetAwaiter().GetResult();
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
