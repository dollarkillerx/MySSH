using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MySSH.Spike.McpHost;

public sealed record ApprovalRequest(string Client, string Host, string Command);

public enum ApprovalDecision { Allow, Deny, Timeout, Cancelled }

/// <summary>Implemented by the GUI. Called from arbitrary thread-pool threads (Kestrel / UDS sessions).</summary>
public interface IApprovalService
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>--auto-approve=allow|deny : for automated tests, no UI.</summary>
public sealed class AutoApprovalService(bool allow) : IApprovalService
{
    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        => Task.FromResult(allow ? ApprovalDecision.Allow : ApprovalDecision.Deny);
}

/// <summary>
/// Desktop implementation: marshals to the Avalonia UI thread, shows a modeless window,
/// and awaits the user's click with a timeout (timeout = deny). Client cancellation closes the window.
/// </summary>
public sealed class AvaloniaApprovalService(SpikeOptions options, EventLog events) : IApprovalService
{
    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Window creation MUST happen on the UI thread. InvokeAsync returns once the window is shown,
        // the actual decision arrives later via tcs (never block the UI thread waiting for it).
        var callerThread = Environment.CurrentManagedThreadId;
        int uiThread = -1;
        var window = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            uiThread = Environment.CurrentManagedThreadId;
            var w = new ApprovalWindow(request, options.ApprovalTimeout, tcs);
            w.Show();
            w.Activate();
            return w;
        });
        events.Write("APPROVAL WINDOW SHOWN", $"host={request.Host} tool-thread={callerThread} window-created-on-thread={uiThread} (UI) timeout={options.ApprovalTimeout.TotalSeconds}s");

        ApprovalDecision decision;
        try
        {
            decision = await tcs.Task.WaitAsync(options.ApprovalTimeout, cancellationToken);
        }
        catch (TimeoutException) { decision = ApprovalDecision.Timeout; }
        catch (OperationCanceledException) { decision = ApprovalDecision.Cancelled; }

        // Close from the UI thread; Post (fire-and-forget) so a busy UI never blocks the tool call.
        Dispatcher.UIThread.Post(() => window.CloseWithoutResult());
        cancellationToken.ThrowIfCancellationRequested(); // propagate client cancellation
        return decision;
    }
}

internal sealed class ApprovalWindow : Window
{
    private readonly TaskCompletionSource<ApprovalDecision> _tcs;
    private readonly DispatcherTimer _timer;
    private bool _closingProgrammatically;

    public ApprovalWindow(ApprovalRequest req, TimeSpan timeout, TaskCompletionSource<ApprovalDecision> tcs)
    {
        _tcs = tcs;
        Title = "MySSH – MCP approval";
        Width = 460; SizeToContent = SizeToContent.Height;
        CanResize = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var deadline = DateTime.UtcNow + timeout;
        var countdown = new TextBlock { Foreground = Brushes.Gray };
        var deny = new Button { Content = "Deny", IsDefault = true, IsCancel = true };
        var allow = new Button { Content = "Allow", Background = Brushes.IndianRed, Foreground = Brushes.White };
        deny.Click += (_, _) => { _tcs.TrySetResult(ApprovalDecision.Deny); Close(); };
        allow.Click += (_, _) => { _tcs.TrySetResult(ApprovalDecision.Allow); Close(); };

        Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"{req.Client} wants to run a command", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                new TextBlock { Text = $"Host: {req.Host}" },
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#262626")), Padding = new Thickness(8),
                    Child = new SelectableTextBlock { Text = req.Command, FontFamily = new FontFamily("Menlo,Consolas,monospace"), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap },
                },
                countdown,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { deny, allow } },
            },
        };

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) =>
        {
            var left = deadline - DateTime.UtcNow;
            countdown.Text = $"Auto-deny in {Math.Max(0, left.TotalSeconds):0}s";
        });
        _timer.Start();

        // Evidence for automated runs: render the dialog itself to PNG (no screen-recording permission,
        // no capture of the rest of the user's desktop).
        if (Environment.GetEnvironmentVariable("MYSSH_SNAPSHOT_DIR") is { Length: > 0 } dir)
            Opened += (_, _) => DispatcherTimer.RunOnce(() =>
            {
                var scale = RenderScaling;
                var size = new PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
                using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
                rtb.Render(this);
#pragma warning disable CS0618 // spike: obsolete overload is fine
                rtb.Save(Path.Combine(dir, "approval-dialog.png"));
#pragma warning restore CS0618
            }, TimeSpan.FromMilliseconds(600));
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (!_closingProgrammatically) _tcs.TrySetResult(ApprovalDecision.Deny); // closing the window = deny
        base.OnClosed(e);
    }

    public void CloseWithoutResult()
    {
        _closingProgrammatically = true;
        if (IsVisible) Close();
    }
}
