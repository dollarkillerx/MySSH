using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ModelContextProtocol.AspNetCore;

namespace MySSH.Spike.McpHost;

public partial class App : Application
{
    private McpHostRuntime? _runtime;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var opts = Program.Options;
            Console.Error.WriteLine($"[app] Avalonia framework initialized at {Program.Uptime.ElapsedMilliseconds}ms (UI thread {Environment.CurrentManagedThreadId})");
            MainWindow? main = null;
            if (opts.Tray)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // approval windows open/close without quitting
            }
            else
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = main = new MainWindow();
            }

            var mode = Environment.GetEnvironmentVariable("MYSSH_HTTP_MODE")?.ToLowerInvariant() switch
            {
                "stateful" => HttpServerSessionMode.Stateful,
                "hybrid" => HttpServerSessionMode.StatefulForInitializeClients,
                _ => HttpServerSessionMode.Stateless,
            };

            // Start Kestrel + UDS off the UI thread so the first frame is not delayed by host startup.
            _ = Task.Run(async () =>
            {
                try
                {
                    _runtime = await McpHostRuntime.StartAsync(opts, mode);
                    // SIGTERM/SIGINT -> ConsoleLifetime -> ApplicationStopping -> quit Avalonia loop.
                    _runtime.Lifetime.ApplicationStopping.Register(() =>
                        Dispatcher.UIThread.Post(() => desktop.Shutdown()));
                    if (main is not null)
                        Dispatcher.UIThread.Post(() => main.ShowEndpoint(_runtime.Endpoint, opts));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[app] host failed: " + ex);
                    Dispatcher.UIThread.Post(() => desktop.Shutdown(2));
                }
            });

            desktop.Exit += (_, _) =>
            {
                // Exit runs on the UI thread: stop the host on the pool (never block UI on something that may Post to it).
                if (_runtime is { } rt)
                    Task.Run(async () => { await rt.StopAsync(); await rt.DisposeAsync(); }).Wait(TimeSpan.FromSeconds(6));
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
