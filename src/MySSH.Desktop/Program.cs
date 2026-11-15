using Avalonia;

namespace MySSH.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--self-test", var legacyDir])
            return Services.SelfTest.RunAsync(legacyDir).GetAwaiter().GetResult();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
#if DEBUG
        .WithDeveloperTools()
#endif
        .LogToTrace();
}
