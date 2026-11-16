using Avalonia;
using MySSH.Core;

namespace MySSH.Desktop;

internal static class Program
{
    static IDisposable? s_instanceLock; // held until the process exits

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--self-test", var legacyDir])
            return Services.SelfTest.RunAsync(legacyDir).GetAwaiter().GetResult();
        // --tray: started by myssh-mcp when MySSH was not running; no window unless the vault needs the master password.
        App.StartInTray = args.Contains("--tray");
        s_instanceLock = Services.SingleInstance.TryAcquire(AppPaths.Default);
        if (s_instanceLock is null)
        {
            Console.Error.WriteLine("MySSH is already running.");
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
#if DEBUG
        .WithDeveloperTools()
#endif
        .LogToTrace();
}
