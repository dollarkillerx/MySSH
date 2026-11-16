using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MySSH.Core;
using MySSH.Desktop;
using MySSH.Desktop.IntegrationTests;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.Desktop.Views;
using MySSH.Ssh;
using MySSH.Ssh.IntegrationTests;
using MySSH.Terminal;

[assembly: AvaloniaTestApplication(typeof(HeadlessApp))]

namespace MySSH.Desktop.IntegrationTests;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>P2 acceptance: terminal and SFTP tabs against a real sshd (see tests/MySSH.Ssh.IntegrationTests/SshdEnvironment.cs).</summary>
[Collection(SshdGroup.Name)]
public sealed class SessionTests(SshdEnvironment env)
{
    static async Task Until(Func<bool> cond, string what, int timeoutMs = 15_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Timed out waiting for: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    static string Row(Host h) => h.Label;

    [Fact(Timeout = 90_000)]
    public async Task Terminal_session_runs_commands_and_reports_exit_code()
    {
        env.RequireSshd();
        Loc.Instance.SetCulture("en-US");
        await using var lab = new Lab(env);
        var host = lab.AddHost("target");
        await using var vm = new TerminalSessionViewModel(lab.Connector(), new FakeUi(), host.Id, host.Label, "—");
        await vm.ConnectAsync(100, 30);
        Assert.Equal(SessionState.Connected, vm.State);
        var output = new StringBuilder();
        vm.Backend!.DataReceived += d => { lock (output) output.Append(Encoding.UTF8.GetString(d.Span)); };
        await vm.Backend.WriteAsync(Encoding.UTF8.GetBytes("echo hel''lo-$((6*7)) && stty size\n"));
        await Until(() => { lock (output) return output.ToString().Contains("hello-42", StringComparison.Ordinal); }, "echo output");
        await Until(() => { lock (output) return output.ToString().Contains("30 100", StringComparison.Ordinal); }, "pty size");
        await vm.Backend.WriteAsync(Encoding.UTF8.GetBytes("exit 3\n"));
        await Until(() => vm.State == SessionState.Ended, "shell exit");
        Assert.Equal("The remote shell exited (code 3).", vm.StatusMessage);
        Assert.True(vm.ShowBanner);
        Assert.False(vm.BannerIsError);

        await vm.ReconnectCommand.ExecuteAsync(null); // reconnect after a normal exit
        Assert.Equal(SessionState.Connected, vm.State);
    }

    [Fact(Timeout = 90_000)]
    public async Task Rejected_host_key_fails_with_a_readable_message()
    {
        env.RequireSshd();
        Loc.Instance.SetCulture("en-US");
        await using var lab = new Lab(env, HostKeyDecision.Reject);
        var host = lab.AddHost("target");
        await using var vm = new TerminalSessionViewModel(lab.Connector(), new FakeUi(), host.Id, host.Label, "—");
        await vm.ConnectAsync(80, 24);
        Assert.Equal(SessionState.Failed, vm.State);
        Assert.Contains("you did not trust the host key", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Null(vm.Backend);
    }

    [Fact(Timeout = 120_000)]
    public async Task Sftp_browse_create_upload_edit_rename_and_symlink_safe_delete()
    {
        env.RequireSshd();
        Loc.Instance.SetCulture("en-US");
        await using var lab = new Lab(env);
        var host = lab.AddHost("target");
        var connector = lab.Connector();
        var ui = new FakeUi();
        await using var vm = new SftpSessionViewModel(connector, ui, host.Id, host.Label, "—");
        await vm.ConnectAsync();
        Assert.Equal(SessionState.Connected, vm.State);

        // work in a fresh directory under the sshd's data dir
        var root = Path.Combine(env.Data, "ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "target.txt"), "keep me");
        File.CreateSymbolicLink(Path.Combine(root, "link.txt"), Path.Combine(root, "target.txt"));
        await vm.NavigateAsync(root);
        Assert.Equal(root, vm.CurrentPath);
        Assert.Equal(["link.txt", "target.txt"], vm.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));

        ui.TextAnswers.Enqueue("newdir");
        await vm.NewFolderCommand.ExecuteAsync(null);
        Assert.Equal("newdir", vm.Entries[0].Name); // folders first
        Assert.True(Directory.Exists(Path.Combine(root, "newdir")));

        var local = Path.Combine(Path.GetTempPath(), $"myssh-ui-{Guid.NewGuid():N}.conf");
        File.WriteAllText(local, "listen 80;\n");
        try
        {
            vm.UploadPaths([local]);
            await Until(() => vm.Transfers.Any(t => t.Item.State == MySSH.Ssh.TransferState.Completed), "upload");
            await Until(() => vm.Entries.Any(e => e.Name == Path.GetFileName(local)), "refresh after upload");
        }
        finally { File.Delete(local); }

        // edit in place
        await vm.OpenCommand.ExecuteAsync(vm.Entries.Single(e => e.Name == Path.GetFileName(local)));
        Assert.True(vm.IsEditing);
        Assert.Equal("listen 80;\n", vm.EditorText);
        vm.EditorText = "listen 443 ssl; # 中文\n";
        await vm.SaveEditorCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditing);
        Assert.Equal("listen 443 ssl; # 中文\n", File.ReadAllText(Path.Combine(root, Path.GetFileName(local))));

        // rename
        ui.TextAnswers.Enqueue("nginx.conf");
        await vm.RenameCommand.ExecuteAsync(vm.Entries.Single(e => e.Name == Path.GetFileName(local)));
        Assert.True(File.Exists(Path.Combine(root, "nginx.conf")));

        // deleting the symlink must keep its target (P0 bug Q7g)
        await vm.DeleteCommand.ExecuteAsync(vm.Entries.Single(e => e.Name == "link.txt"));
        Assert.Contains("The file it points to is kept", ui.Confirms[^1], StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "link.txt")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(root, "target.txt")));

        // recursive folder delete
        File.WriteAllText(Path.Combine(root, "newdir", "inner"), "x");
        await vm.DeleteCommand.ExecuteAsync(vm.Entries.Single(e => e.Name == "newdir"));
        Assert.False(Directory.Exists(Path.Combine(root, "newdir")));
        Assert.Null(vm.Error);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Sessions_page_renders_a_live_terminal_and_keeps_it_across_tab_switches()
    {
        env.RequireSshd();
        Loc.Instance.SetCulture("en-US");
        await using var lab = new Lab(env);
        var host = lab.AddHost("target");
        var connector = lab.Connector();
        var ws = new SessionsWorkspaceViewModel(() => connector, new FakeUi());
        var page = new SessionsPage { DataContext = ws };
        var w = new Window { Width = 1100, Height = 640, Content = page };
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        w.Show();
        var row = new HostRowViewModel(host.Id, host.Label, "127.0.0.1", env.User, "Key", "—", AiPolicy.Off, "Off", []);
        var term = (TerminalSessionViewModel)ws.Open(row, SessionKind.Terminal);
        await Until(() => term.State == SessionState.Connected, "terminal connect");
        var view = w.GetVisualDescendants().OfType<TerminalView>().Single();
        await term.Backend!.WriteAsync(Encoding.UTF8.GetBytes("clear; printf '\\033[1;32mMySSH\\033[0m 端到端 ok\\n'\n"));
        await Until(() => view.GetVisibleText(40).Contains("端到端 ok", StringComparison.Ordinal), "rendered output");
        Save(w, "p2-terminal-live-dark");

        var sftp = ws.Open(row, SessionKind.Sftp);
        await Until(() => sftp.State == SessionState.Connected, "sftp connect");
        Save(w, "p2-sftp-dark");
        ws.SelectedTab = term; // switching back must keep the same TerminalView and its screen
        Pump();
        Assert.Same(view, w.GetVisualDescendants().OfType<TerminalView>().Single(v => v.IsEffectivelyVisible));
        Assert.Contains("端到端 ok", view.GetVisibleText(40), StringComparison.Ordinal);

        await ws.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Open_sftp_from_a_terminal_opens_next_to_it_in_the_shells_directory()
    {
        env.RequireSshd();
        Loc.Instance.SetCulture("zh-CN");
        await using var lab = new Lab(env);
        var host = lab.AddHost("target");
        var connector = lab.Connector();
        var ws = new SessionsWorkspaceViewModel(() => connector, new FakeUi());
        var page = new SessionsPage { DataContext = ws };
        var w = new Window { Width = 1100, Height = 640, Content = page };
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        w.Show();
        var row = new HostRowViewModel(host.Id, host.Label, "127.0.0.1", env.User, "Key", "—", AiPolicy.Off, "Off", []);
        var term = (TerminalSessionViewModel)ws.Open(row, SessionKind.Terminal);
        var other = ws.Open(row, SessionKind.Terminal); // a later tab: SFTP must be inserted right after `term`
        ws.SelectedTab = term;
        await Until(() => term.State == SessionState.Connected, "terminal connect");

        // the shell reports its directory with OSC 7 (what bash/zsh integrations do on every prompt)
        var dir = Path.Combine(env.Data, "osc7 目录");
        Directory.CreateDirectory(dir);
        var uri = new Uri("file://localhost" + dir).AbsoluteUri;
        await term.Backend!.WriteAsync(Encoding.UTF8.GetBytes($"cd '{dir}' && printf '\\033]7;%s\\007' '{uri}'\n"));
        await Until(() => term.WorkingDirectory == dir, "OSC 7 working directory");

        var button = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "OpenSftpButton" && b.IsEffectivelyVisible);
        button.Command!.Execute(null);
        var sftp = Assert.IsType<SftpSessionViewModel>(ws.SelectedTab);
        Assert.Equal(ws.Tabs.IndexOf(term) + 1, ws.Tabs.IndexOf(sftp));
        Assert.Equal(ws.Tabs.Count - 1, ws.Tabs.IndexOf(other));
        await Until(() => sftp.State == SessionState.Connected, "sftp connect");
        await Until(() => sftp.CurrentPath == dir, "sftp in the terminal's directory");
        Save(w, "p3-open-sftp-from-terminal-light-zh");
        // breadcrumb: "/ var / folders / _b …": no doubled slash after the root, underscores kept (no access-key mangling)
        Assert.False(sftp.Breadcrumb[0].ShowSeparator);
        Assert.False(sftp.Breadcrumb[1].ShowSeparator);
        Assert.True(sftp.Breadcrumb[2].ShowSeparator);
        if (sftp.Breadcrumb.FirstOrDefault(b => b.Name.StartsWith('_')) is { } underscored)
            Assert.Contains(w.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == underscored.Name);

        await ws.DisposeAsync();
        Loc.Instance.SetCulture("en-US");
        w.Close();
    }

    static void Pump()
    {
        for (var i = 0; i < 30; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    }

    static void Save(Window w, string name, [CallerFilePath] string here = "")
    {
        Pump();
        var dir = Path.Combine(Path.GetDirectoryName(here)!, ".work", "screenshots");
        Directory.CreateDirectory(dir);
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}

sealed class FakeUi : IUiServices
{
    public Queue<string?> TextAnswers { get; } = new();
    public List<string> Confirms { get; } = [];
    public Task<bool> ConfirmAsync(string heading, string body, string primaryText, bool danger) { Confirms.Add(body); return Task.FromResult(true); }
    public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter>? filters = null) => Task.FromResult<string?>(null);
    public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter>? filters = null) => Task.FromResult<string?>(null);
    public Task<string?> PromptPasswordAsync(string heading, string body, bool confirm) => Task.FromResult<string?>(null);
    public string? Clipboard { get; private set; }
    public Task CopyToClipboardAsync(string text) { Clipboard = text; return Task.CompletedTask; }
    public Task<string?> PromptTextAsync(string heading, string label, string initialValue) => Task.FromResult(TextAnswers.Count > 0 ? TextAnswers.Dequeue() : null);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task OpenUriAsync(Uri uri) => Task.CompletedTask;
}
