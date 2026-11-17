using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.ViewModels;
using MySSH.Desktop.Views;
using MySSH.Ssh;

namespace MySSH.Desktop.Tests;

/// <summary>Lifetime and UI-thread regressions found by the code review (tabs closed mid-connect, incremental Markdown).</summary>
[Collection("Loc")]
public sealed class LifetimeTests
{
    /// <summary>A connect that is already past its last cancellation check: it completes even after the tab closed.</summary>
    sealed class SlowConnector : ISshConnector
    {
        public TaskCompletionSource<ISshConnection> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ISshConnection> ConnectAsync(Guid hostId, ConnectionOrigin origin, CancellationToken ct = default) => Pending.Task;
    }

    sealed class TrackedConnection : ISshConnection
    {
        public bool Disposed { get; private set; }
        public int Opened { get; private set; }
        public Host Host { get; } = new() { Label = "web", Hostname = "203.0.113.10" };
        public ConnectionOrigin Origin => ConnectionOrigin.User;
        public SshConnectionState State => SshConnectionState.Connected;
        public event Action<SshConnectionStateChange>? StateChanged { add { } remove { } }
        public Task<ITerminalBackend> OpenShellAsync(int columns, int rows, string term = "xterm-256color", CancellationToken ct = default)
        {
            Opened++;
            throw new InvalidOperationException("must not open a shell for a closed tab");
        }
        public Task<ExecResult> ExecAsync(string command, ExecOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ISftpSession> OpenSftpAsync(CancellationToken ct = default)
        {
            Opened++;
            throw new InvalidOperationException("must not open SFTP for a closed tab");
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [AvaloniaFact]
    public async Task A_terminal_tab_closed_while_connecting_releases_the_late_connection()
    {
        Loc.Instance.SetCulture("en-US");
        var connector = new SlowConnector();
        var tab = new TerminalSessionViewModel(connector, new FakeUi(), Guid.NewGuid(), "web", "");
        var connect = tab.ConnectAsync(80, 24);
        await tab.DisposeAsync(); // the user closes the tab while it says "Connecting…"
        var conn = new TrackedConnection();
        connector.Pending.SetResult(conn);
        await connect;
        Assert.True(conn.Disposed);
        Assert.Equal(0, conn.Opened);
        Assert.Null(tab.Backend);
        Assert.Equal(SessionState.Ended, tab.State);
    }

    [AvaloniaFact]
    public async Task An_sftp_tab_closed_while_connecting_releases_the_late_connection()
    {
        Loc.Instance.SetCulture("en-US");
        var connector = new SlowConnector();
        var tab = new SftpSessionViewModel(connector, new FakeUi(), Guid.NewGuid(), "web", "");
        var connect = tab.ConnectAsync();
        await tab.DisposeAsync();
        var conn = new TrackedConnection();
        connector.Pending.SetResult(conn);
        await connect;
        Assert.True(conn.Disposed);
        Assert.Equal(0, conn.Opened);
        Assert.Equal(SessionState.Ended, tab.State);
    }

    [AvaloniaFact]
    public void Markdown_updates_keep_the_controls_of_unchanged_blocks()
    {
        var view = new MarkdownView { Markdown = "First paragraph.\n\nSecond" };
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();
        view.RenderNow();
        var first = view.Children[0];
        var second = view.Children[1];

        view.Markdown = "First paragraph.\n\nSecond, still streaming"; // only the last block grew
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, view.Children.Count);
        Assert.Same(first, view.Children[0]); // kept: a selection in it survives, and it is not laid out again
        Assert.NotSame(second, view.Children[1]);
        window.Close();
    }
}
