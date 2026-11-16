using Avalonia.Threading;
using MySSH.Desktop.ViewModels;
using MySSH.Tools;

namespace MySSH.Desktop.Services;

/// <summary>
/// Exposes the open terminal tabs to the tool layer (list_sessions / read_terminal / send_to_terminal). Tool calls arrive
/// on the thread pool; every access to view models and TerminalViews is marshalled to the UI thread.
/// </summary>
public sealed class TerminalSessionRegistry(Func<SessionsWorkspaceViewModel?> workspace) : ITerminalSessionRegistry
{
    static T OnUi<T>(Func<T> f) => Dispatcher.UIThread.CheckAccess() ? f() : Dispatcher.UIThread.InvokeAsync(f).GetAwaiter().GetResult();

    static Task<T> OnUiAsync<T>(Func<T> f, CancellationToken ct) =>
        Dispatcher.UIThread.CheckAccess() ? Task.FromResult(f()) : Dispatcher.UIThread.InvokeAsync(f, DispatcherPriority.Normal, ct).GetTask();

    IEnumerable<TerminalSessionViewModel> Terminals() => workspace()?.Tabs.OfType<TerminalSessionViewModel>() ?? [];

    TerminalSessionViewModel? Find(string id) => Terminals().FirstOrDefault(t => t.SessionId == id);

    public IReadOnlyList<TerminalSessionInfo> List() => OnUi(() => (IReadOnlyList<TerminalSessionInfo>)Terminals()
        .Select(t => new TerminalSessionInfo(t.SessionId, t.HostId, t.HostLabel, t.Title, t.IsConnected, t.Columns, t.RowCount))
        .ToList());

    public Task<string?> ReadAsync(string sessionId, int lines, CancellationToken ct = default) =>
        OnUiAsync(() => Find(sessionId)?.Screen?.GetRecentText(lines), ct);

    public Task<bool> SendAsync(string sessionId, string text, bool execute, CancellationToken ct = default) =>
        OnUiAsync(() => Find(sessionId) is { IsConnected: true, Screen: { } screen } && screen.TypeText(text, execute), ct);
}
