using System.Globalization;
using Avalonia.Threading;
using MySSH.Desktop.Localization;
using MySSH.Tools;
using MySSH.UI.Carbon;

namespace MySSH.Desktop;

/// <summary>
/// MCP tool approvals (design §6.1): a modal per request, queued behind the other security prompts. Called on the thread
/// pool by the tool layer; the modal is driven on the UI thread and the caller awaits off it. Closing the modal, Esc or
/// "Deny" deny; the deadline and caller cancellation close it (the tool layer maps the deadline to TimedOut).
/// </summary>
public sealed partial class MainWindow : IApprovalService
{
    public async Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        try
        {
            return await OneAtATimeAsync(() =>
            {
                if (ct.IsCancellationRequested) return Task.FromResult(ApprovalOutcome.Cancelled); // gave up while queued
                return Dispatcher.UIThread.InvokeAsync(() => ShowApprovalAsync(request, ct));
            });
        }
        catch (OperationCanceledException) { return ApprovalOutcome.Cancelled; }
    }

    /// <summary>Catalog titles are English (they are written for the model); the UI shows its own translation.</summary>
    internal static string LocalizedToolTitle(ToolDefinition tool) =>
        Loc.Instance.TryGet("tool." + tool.Name) ?? tool.Title;

    /// <summary>The reason in the user's language from the machine-readable kind; the English text is the fallback.</summary>
    internal static string LocalizedReason(ApprovalRequest r)
    {
        var loc = Loc.Instance;
        return r.ReasonKind switch
        {
            ApprovalReason.HighRisk => loc.Format("approval.reason.HighRisk", ("rule", r.RiskRule ?? "")),
            ApprovalReason.AskPolicy => loc["approval.reason.AskPolicy"],
            ApprovalReason.Overwrite => loc["approval.reason.Overwrite"],
            ApprovalReason.Delete => loc["approval.reason.Delete"],
            ApprovalReason.TerminalExecute => loc["approval.reason.TerminalExecute"],
            _ => r.Reason ?? "",
        };
    }

    async Task<ApprovalOutcome> ShowApprovalAsync(ApprovalRequest r, CancellationToken ct)
    {
        var loc = Loc.Instance;
        ApprovalModal.Heading = loc[r.HighRisk ? "approval.titleHighRisk" : "approval.title"];
        ApprovalModal.Label = r.Actor.DisplayName;
        ApprovalModal.IsDanger = r.HighRisk;
        ApprovalModal.PrimaryButtonText = loc["approval.allow"];
        ApprovalModal.SecondaryButtonText = loc["approval.deny"];
        ApprovalRisk.IsVisible = r.HighRisk;
        ApprovalRisk.Title = loc["approval.highRisk"];
        var reason = LocalizedReason(r);
        ApprovalRisk.Subtitle = reason;
        ApprovalBody.Text = loc.Format("approval.body", ("client", r.Actor.DisplayName));
        ApprovalClient.Text = r.Actor.DisplayName;
        ApprovalTool.Text = $"{LocalizedToolTitle(r.Tool)} ({r.Tool.Name})";
        ApprovalHost.Text = $"{r.Host.Label} ({r.Host.Hostname})";
        ApprovalSummary.Text = string.IsNullOrEmpty(r.Details) ? r.Summary : r.Summary + "\n\n" + r.Details;
        ApprovalReasonText.Text = r.HighRisk ? "" : reason;
        ApprovalReasonText.IsVisible = ApprovalReasonText.Text.Length > 0;

        void Tick()
        {
            var left = Math.Max(0, (int)Math.Ceiling((r.Deadline - DateTimeOffset.UtcNow).TotalSeconds));
            ApprovalCountdown.Text = loc.Format("approval.countdown", ("seconds", left.ToString(CultureInfo.InvariantCulture)));
            if (left == 0) ApprovalModal.Close(); // the tool layer also enforces the deadline
        }
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick());
        Tick();

        var tcs = new TaskCompletionSource<ModalResult>();
        void OnClosed(object? s, ModalClosedEventArgs e) => tcs.TrySetResult(e.Result);
        ApprovalModal.Closed += OnClosed;
        await using var reg = ct.Register(() => Dispatcher.UIThread.Post(() => ApprovalModal.Close()));
        ApprovalModal.IsOpen = true;
        timer.Start();
        try
        {
            var result = await tcs.Task;
            if (ct.IsCancellationRequested) return ApprovalOutcome.Cancelled;
            if (result == ModalResult.Primary) return ApprovalOutcome.Approved;
            return DateTimeOffset.UtcNow >= r.Deadline ? ApprovalOutcome.TimedOut : ApprovalOutcome.Denied;
        }
        finally
        {
            timer.Stop();
            ApprovalModal.Closed -= OnClosed;
        }
    }
}
