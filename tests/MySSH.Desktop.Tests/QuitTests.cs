using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MySSH.Desktop.Services;

namespace MySSH.Desktop.Tests;

/// <summary>Regression: Quit hung forever once a session was open (UI thread blocked on cleanup that resumes on it).</summary>
public sealed class QuitTests : IDisposable
{
    readonly string _logs = Directory.CreateTempSubdirectory("myssh-quit-").FullName;

    public QuitTests() => CrashLog.DirectoryOverride = _logs;

    public void Dispose()
    {
        CrashLog.DirectoryOverride = null;
        Directory.Delete(_logs, recursive: true);
    }

    static bool Pump(Task task, TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        return task.IsCompleted;
    }

    [AvaloniaFact]
    public void Cleanup_that_resumes_on_the_UI_thread_finishes()
    {
        var stopped = false;
        var disposed = false;
        async ValueTask DisposeLikeSessions()
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            await Task.Delay(50); // continuation is posted back to the UI thread, like closing an SSH session tab
            Assert.True(Dispatcher.UIThread.CheckAccess());
            disposed = true;
        }
        var quit = App.CleanupForQuitAsync(() => { stopped = true; return Task.CompletedTask; }, DisposeLikeSessions, TimeSpan.FromSeconds(10));
        Assert.True(Pump(quit, TimeSpan.FromSeconds(10)), "quit cleanup did not complete");
        Assert.True(quit.Result);
        Assert.True(stopped);
        Assert.True(disposed);
    }

    /// <summary>The quit request itself waits (it is never cancelled, which would abort a macOS logout / restart).</summary>
    [AvaloniaFact]
    public void Blocking_wait_on_the_UI_thread_runs_UI_continuations()
    {
        var disposed = false;
        async ValueTask DisposeLikeSessions()
        {
            await Task.Delay(50); // resumes on the UI thread: a plain .Wait() here deadlocks
            Assert.True(Dispatcher.UIThread.CheckAccess());
            disposed = true;
        }
        var cleanup = App.CleanupForQuitAsync(() => Task.CompletedTask, DisposeLikeSessions, TimeSpan.FromSeconds(10));
        var started = DateTime.UtcNow;
        App.WaitWithDispatcher(cleanup);
        Assert.True(cleanup.IsCompleted);
        Assert.True(cleanup.Result);
        Assert.True(disposed);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [AvaloniaFact]
    public void Quit_goes_ahead_when_cleanup_hangs_or_fails()
    {
        var never = new TaskCompletionSource();
        var hung = App.CleanupForQuitAsync(() => Task.CompletedTask, () => new ValueTask(never.Task), TimeSpan.FromMilliseconds(200));
        Assert.True(Pump(hung, TimeSpan.FromSeconds(5)));
        Assert.False(hung.Result);

        var failed = App.CleanupForQuitAsync(() => throw new InvalidOperationException("mcp"), () => ValueTask.CompletedTask, TimeSpan.FromSeconds(5));
        Assert.True(Pump(failed, TimeSpan.FromSeconds(5)));
        Assert.False(failed.Result);
        Assert.Equal(2, Directory.GetFiles(_logs, "crash-*.log").Length); // both logged, in the test folder
    }
}
