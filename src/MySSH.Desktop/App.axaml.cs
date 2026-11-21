using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.Storage;

namespace MySSH.Desktop;

public sealed partial class App : Application, IDisposable
{
    TrayIcon? _tray;
    bool _quitting, _cleanedUp;
    ShellViewModel? _shell;

    /// <summary>Longest a Quit waits for sessions and the MCP server to close before exiting anyway.</summary>
    internal static TimeSpan QuitTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Set by Program for <c>--tray</c> (launched by myssh-mcp): start hidden, serve MCP from the tray.</summary>
    public static bool StartInTray { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => _tray?.Dispose();

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplyFonts();
            Loc.Instance.CultureChanged += () => Dispatcher.UIThread.Post(ApplyFonts);
            var shell = new ShellViewModel(ct => AppSession.OpenAsync(StorageOptions.Default, ct)) { Updater = new VelopackUpdater() };
            shell.QuitRequested = () => Quit(desktop);
            // An exception escaping a UI event or async void handler would abort the whole app (and in NativeAOT its
            // message only goes to stderr). Log it, tell the user, keep running.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                var path = CrashLog.Write(e.Exception, "UI thread");
                e.Handled = true;
                shell.ReportUnexpectedError(e.Exception, path);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                CrashLog.Write(e.Exception, "unobserved task");
                e.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception, "fatal");
            var window = new MainWindow { DataContext = shell };
            var initialized = false;
            async Task InitializeOnceAsync()
            {
                if (initialized) return;
                initialized = true;
                await shell.InitializeAsync();
            }
            window.Opened += async (_, _) => await InitializeOnceAsync();
            // While the MCP server runs, closing the window keeps MySSH in the tray (design §10); Quit comes from the
            // tray menu or the app menu (Cmd+Q), which go through ShutdownRequested.
            window.Closing += (_, e) =>
            {
                if (_quitting || !shell.KeepRunningInTray) return;
                e.Cancel = true;
                window.Hide();
                ShowTray(desktop, window);
            };
            // Cmd+Q / the Dock / logout / restart arrive here. The request is never cancelled (that would abort a system
            // logout or restart); the async cleanup runs to completion inside it, see RunQuitCleanup.
            _shell = shell;
            desktop.ShutdownRequested += (_, _) => RunQuitCleanup(shell);
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable) // macOS: Dock icon click brings the window back
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen) ShowWindow(window);
                };
            desktop.MainWindow = window;
            if (StartInTray)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ShowTray(desktop, window);
                _ = StartHiddenAsync(shell, window, InitializeOnceAsync);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Tray start: unlock (OS keystore) and start MCP without a window. The window only appears when the user is needed:
    /// the vault wants the master password, or the MCP server is off / failed (the waiting bridge would time out).
    /// </summary>
    static async Task StartHiddenAsync(ShellViewModel shell, Window window, Func<Task> initialize)
    {
        try
        {
            await initialize();
            if (shell.IsVaultPromptOpen || shell.StartupFailed)
            {
                ShowWindow(window);
                return;
            }
            await shell.McpStartup; // loads the MCP settings and starts the server on the thread pool
            if (!shell.KeepRunningInTray)
            {
                shell.SelectedNav = shell.NavItems.First(n => n.Page == AppPage.Ai);
                ShowWindow(window);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Nobody awaits this task: report and show the window instead of staying invisible in the tray.
            shell.ReportUnexpectedError(e, CrashLog.Write(e, "tray start"));
            ShowWindow(window);
        }
    }

    /// <summary>CJK fallback fonts follow the UI language (see CarbonFonts: avoids mixed Chinese / Japanese / Korean glyphs).</summary>
    void ApplyFonts() => MySSH.UI.Carbon.CarbonFonts.Apply(Resources, MySSH.UI.Carbon.CarbonFonts.PreferredScript(Loc.Instance.Culture, s_osLanguages.Value));

    static readonly Lazy<List<string>> s_osLanguages = new(OsLanguages); // read once

    /// <summary>The OS preferred languages, most preferred first (macOS AppleLanguages; elsewhere the installed UI culture).</summary>
    static List<string> OsLanguages()
    {
        var langs = new List<string>();
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/defaults", "read -g AppleLanguages")
                    { RedirectStandardOutput = true, UseShellExecute = false });
                if (p is not null)
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2000);
                    langs.AddRange(output.Split(['\n', ',', '(', ')', '"', ' '], StringSplitOptions.RemoveEmptyEntries));
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        }
        langs.Add(System.Globalization.CultureInfo.InstalledUICulture.Name);
        return langs;
    }

    static void ShowWindow(Window window)
    {
        window.Show();
        window.Activate();
    }

    /// <summary>Stops MCP and closes sessions (awaited on the UI thread, bounded by <see cref="QuitTimeout"/>), then exits.</summary>
    /// <summary>Stops MCP and closes sessions before exit (once; bounded by <see cref="QuitTimeout"/>).</summary>
    void RunQuitCleanup(ShellViewModel shell)
    {
        if (_cleanedUp) return;
        _quitting = true;
        _cleanedUp = true;
        WaitWithDispatcher(CleanupForQuitAsync(shell.StopBackgroundServicesAsync, shell.DisposeAsync, QuitTimeout));
        Dispose();
    }

    /// <summary>
    /// Waits for <paramref name="task"/> on the UI thread without deadlocking: a nested dispatcher frame keeps running UI
    /// jobs, so continuations that resume on the UI thread (closing session tabs) complete. A plain blocking wait here
    /// hung Quit as soon as a session was open.
    /// </summary>
    internal static void WaitWithDispatcher(Task task)
    {
        if (task.IsCompleted) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            task.GetAwaiter().GetResult();
            return;
        }
        var frame = new DispatcherFrame();
        // Ending the frame from a UI job also wakes the loop if it is idle.
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false, DispatcherPriority.Send), TaskScheduler.Default);
        Dispatcher.UIThread.PushFrame(frame);
    }

    /// <summary>
    /// Runs the quit cleanup without blocking the calling (UI) thread. Returns false when it failed or timed out (logged);
    /// Quit goes ahead either way.
    /// </summary>
    internal static async Task<bool> CleanupForQuitAsync(Func<Task> stopBackground, Func<ValueTask> dispose, TimeSpan timeout)
    {
        async Task CleanupAsync()
        {
            await Task.Run(stopBackground); // the MCP host stops on the thread pool (spike G2)
            await dispose();
        }
        try
        {
            var cleanup = CleanupAsync();
            if (await Task.WhenAny(cleanup, Task.Delay(timeout)) == cleanup)
            {
                await cleanup;
                return true;
            }
            CrashLog.Write(new TimeoutException($"Quit cleanup did not finish within {timeout.TotalSeconds:0} s."), "quit");
        }
        catch (Exception e)
        {
            CrashLog.Write(e, "quit");
        }
        return false;
    }

    /// <summary>Tray "Quit" and "Restart to update".</summary>
    void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_shell is { } shell) RunQuitCleanup(shell); // Shutdown() is a forced exit: it may skip ShutdownRequested
        desktop.Shutdown();
    }

    void ShowTray(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        if (_tray is null)
        {
            // Bound to Loc, so the menu follows language changes while it exists.
            var show = new NativeMenuItem();
            show.Bind(NativeMenuItem.HeaderProperty, Loc.Instance.Bind("tray.show"));
            show.Click += (_, _) => ShowWindow(window);
            var quit = new NativeMenuItem();
            quit.Bind(NativeMenuItem.HeaderProperty, Loc.Instance.Bind("tray.quit"));
            quit.Click += (_, _) => Quit(desktop);
            _tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MySSH/Assets/icon.png"))),
                Menu = [show, new NativeMenuItemSeparator(), quit],
            };
            _tray.Bind(TrayIcon.ToolTipTextProperty, Loc.Instance.Bind("tray.tooltip"));
            _tray.Clicked += (_, _) => ShowWindow(window);
            TrayIcon.SetIcons(this, [_tray]);
        }
        _tray.IsVisible = true;
    }
}
