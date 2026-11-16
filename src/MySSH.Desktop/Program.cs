using Avalonia;
using MySSH.Core;

namespace MySSH.Desktop;

internal static class Program
{
    static IDisposable? s_instanceLock; // held until the process exits

    [STAThread]
    public static int Main(string[] args)
    {
        // First: Velopack's install / update hooks run here and may exit the process. A no-op when not installed.
        Velopack.VelopackApp.Build().Run();
        ClearProviderEnvironment();
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

    /// <summary>
    /// AI provider settings come only from MySSH's own configuration. A terminal-launched MySSH would otherwise inherit
    /// e.g. a Claude Code proxy's ANTHROPIC_BASE_URL / ANTHROPIC_CUSTOM_HEADERS (design §7.2, spike ④ §4).
    /// </summary>
    internal static void ClearProviderEnvironment()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.OfType<string>())
            if (name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable(name, null);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
#if DEBUG
        .WithDeveloperTools()
#endif
        .LogToTrace();
}
