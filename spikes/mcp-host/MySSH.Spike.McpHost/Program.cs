using System.Diagnostics;
using Avalonia;

namespace MySSH.Spike.McpHost;

class Program
{
    public static readonly Stopwatch Uptime = Stopwatch.StartNew();
    public static SpikeOptions Options { get; private set; } = new();

    [STAThread]
    public static void Main(string[] args)
    {
        Options = SpikeOptions.Parse(args);
        Directory.CreateDirectory(Options.WorkDir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Options.WorkDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        Console.Error.WriteLine($"[main] Avalonia lifetime ended at {Uptime.ElapsedMilliseconds}ms, exiting");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = !Options.Tray })
            .WithInterFont()
            .LogToTrace();
}
