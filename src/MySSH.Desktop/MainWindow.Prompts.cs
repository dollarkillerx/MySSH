using Avalonia.Interactivity;
using Avalonia.Threading;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.UI.Carbon;

namespace MySSH.Desktop;

/// <summary>SSH-layer prompts. Called from SSH.NET threads, so everything marshals to the UI thread.</summary>
public sealed partial class MainWindow : IHostKeyVerifier, ICredentialPrompt
{
    // One security prompt at a time (a jump chain asks once per hop): prompts queue behind each other.
    readonly Lock _promptLock = new();
    Task _promptTail = Task.CompletedTask;

    async Task<T> OneAtATimeAsync<T>(Func<Task<T>> show)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_promptLock) { previous = _promptTail; _promptTail = done.Task; }
        try
        {
            await previous;
            return await show();
        }
        finally { done.SetResult(); }
    }

    public async ValueTask<HostKeyDecision> VerifyAsync(HostKeyCheck check, CancellationToken ct) =>
        await OneAtATimeAsync(() => Dispatcher.UIThread.InvokeAsync(() => ShowHostKeyAsync(check, ct)));

    async Task<HostKeyDecision> ShowHostKeyAsync(HostKeyCheck c, CancellationToken ct)
    {
        var loc = Loc.Instance;
        var changed = c.Status == HostKeyStatus.Changed;
        var hop = c.HopCount > 1
            ? " " + loc.Format(c.HopIndex == c.HopCount - 1 ? "hostKey.hopTarget" : "hostKey.hopJump", ("n", c.HopIndex + 1), ("total", c.HopCount))
            : "";
        HostKeyModal.Heading = loc[changed ? "hostKey.changedTitle" : "hostKey.unknownTitle"];
        HostKeyModal.Label = c.HostLabel;
        HostKeyModal.IsDanger = changed;
        HostKeyBody.Text = loc[changed ? "hostKey.changedBody" : "hostKey.unknownBody"] + hop;
        HostKeyHost.Text = c.HostPort;
        HostKeyType.Text = c.KeyType;
        HostKeyFingerprint.Text = c.Fingerprint;
        HostKeyPrevious.Text = c.PreviousFingerprint ?? "";
        HostKeyPrevious.IsVisible = HostKeyPreviousLabel.IsVisible = changed;
        HostKeyRemember.IsChecked = true;
        HostKeyVerified.IsChecked = false;
        HostKeyVerified.IsVisible = changed;
        HostKeyModal.PrimaryButtonText = loc[changed ? "hostKey.replaceAndConnect" : "hostKey.trustAndConnect"];
        HostKeyModal.SecondaryButtonText = loc["common.cancel"];
        // A changed key can only be accepted after the user ticks "I verified the new fingerprint".
        HostKeyModal.IsPrimaryButtonEnabled = !changed;
        void OnVerified(object? s, RoutedEventArgs e) => HostKeyModal.IsPrimaryButtonEnabled = HostKeyVerified.IsChecked == true;
        HostKeyVerified.IsCheckedChanged += OnVerified;

        var tcs = new TaskCompletionSource<ModalResult>();
        void OnClosed(object? s, ModalClosedEventArgs e) => tcs.TrySetResult(e.Result);
        HostKeyModal.Closed += OnClosed;
        await using var reg = ct.Register(() => Dispatcher.UIThread.Post(() => HostKeyModal.Close()));
        HostKeyModal.IsOpen = true;
        try
        {
            var r = await tcs.Task;
            if (r != ModalResult.Primary) return HostKeyDecision.Reject;
            return HostKeyRemember.IsChecked == true ? HostKeyDecision.AcceptAndSave : HostKeyDecision.AcceptOnce;
        }
        finally
        {
            HostKeyModal.Closed -= OnClosed;
            HostKeyVerified.IsCheckedChanged -= OnVerified;
        }
    }

    public async ValueTask<string?> PromptPassphraseAsync(Identity identity, string? previousAttemptError, CancellationToken ct) =>
        await OneAtATimeAsync(() => Dispatcher.UIThread.InvokeAsync(async () =>
            {
                PassphraseModal.Heading = Loc.Instance["passphrase.title"];
                PassphraseModal.Label = identity.Label;
                PassphraseModal.PrimaryButtonText = Loc.Instance["terminal.reconnect"];
                PassphraseModal.SecondaryButtonText = Loc.Instance["common.cancel"];
                PassphraseBody.Text = Loc.Instance["passphrase.body"];
                PassphraseBox.Text = "";
                Field.SetLabel(PassphraseBox, Loc.Instance["serverForm.passphrase"]);
                Field.SetIsInvalid(PassphraseBox, previousAttemptError is not null);
                Field.SetInvalidText(PassphraseBox, previousAttemptError);
                var tcs = new TaskCompletionSource<ModalResult>();
                void OnClosed(object? s, ModalClosedEventArgs e) => tcs.TrySetResult(e.Result);
                PassphraseModal.Closed += OnClosed;
                await using var reg = ct.Register(() => Dispatcher.UIThread.Post(() => PassphraseModal.Close()));
                PassphraseModal.IsOpen = true;
                PassphraseBox.Focus();
                try
                {
                    var r = await tcs.Task;
                    var text = PassphraseBox.Text;
                    PassphraseBox.Text = "";
                    return r == ModalResult.Primary ? text : null;
                }
                finally { PassphraseModal.Closed -= OnClosed; }
            }));
}
