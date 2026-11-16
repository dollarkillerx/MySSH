using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;

namespace MySSH.Desktop.ViewModels;

/// <summary>The "AI / MCP" page: MCP server settings and the tool audit log (P3); the AI assistant settings join in P4.</summary>
public sealed partial class AiMcpPageViewModel(McpSettingsViewModel mcp, AuditLogViewModel audit) : ObservableObject
{
    public McpSettingsViewModel Mcp { get; } = mcp;
    public AuditLogViewModel Audit { get; } = audit;
    [ObservableProperty] public partial int SelectedTab { get; set; }

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 1) _ = Audit.LoadAsync();
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Mcp.Refresh();
        if (SelectedTab == 1) await Audit.LoadAsync(ct);
    }
}

public sealed partial class McpClientRowViewModel(McpClientRow row) : ObservableObject
{
    public McpClientRow Row { get; } = row;
    public string Name => Row.Name;
    public string Transport => Row.Transport == "local" ? Loc.Instance["mcp.transport.local"] : "HTTP";
    public string LastSeen => Row.LastSeen.ToLocalTime().ToString("T", Loc.Instance.Culture);
    public string Calls => Row.InFlight > 0 ? Loc.Instance.Format("mcp.clients.callsActive", ("calls", Row.Calls), ("active", Row.InFlight)) : Row.Calls.ToString(Loc.Instance.Culture);
    public bool CanDisconnect => Row.CanDisconnect;
}

/// <summary>MCP server switch, endpoint / token, one-click client configuration, connected clients (design §6.4).</summary>
public sealed partial class McpSettingsViewModel : ObservableObject
{
    readonly IMcpController _mcp;
    readonly IUiServices _ui;
    readonly SynchronizationContext? _sync = SynchronizationContext.Current;
    bool _syncing;

    public McpSettingsViewModel(IMcpController mcp, IUiServices ui)
    {
        _mcp = mcp;
        _ui = ui;
        _mcp.Changed += () =>
        {
            if (_sync is null || _sync == SynchronizationContext.Current) Refresh();
            else _sync.Post(_ => Refresh(), null);
        };
        Refresh();
    }

    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string Endpoint { get; set; } = "";
    [ObservableProperty] public partial string SocketPath { get; set; } = "";
    [ObservableProperty] public partial bool IsTokenRevealed { get; set; }
    [ObservableProperty] public partial string TokenDisplay { get; set; } = "";
    [ObservableProperty] public partial string ClaudeCodeCommand { get; set; } = "";
    [ObservableProperty] public partial string ClaudeDesktopJson { get; set; } = "";
    [ObservableProperty] public partial bool HasBridge { get; set; }
    public ObservableCollection<McpClientRowViewModel> Clients { get; } = [];
    [ObservableProperty] public partial bool HasClients { get; set; }
    public string StatusText => Error is not null ? Loc.Instance["mcp.status.error"]
        : IsRunning ? Loc.Instance["mcp.status.running"] : Loc.Instance["mcp.status.stopped"];

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StatusText));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(StatusText));
    partial void OnIsTokenRevealedChanged(bool value) => UpdateToken();

    void UpdateToken() => TokenDisplay = _mcp.Token is not { } t ? "" : IsTokenRevealed ? t : new string('•', 24);

    public void Refresh()
    {
        _syncing = true;
        try
        {
            IsEnabled = _mcp.IsEnabled;
            IsRunning = _mcp.IsRunning;
            Error = _mcp.StartError;
            Endpoint = _mcp.HttpEndpoint?.ToString() ?? "";
            SocketPath = _mcp.LocalSocketPath ?? "";
            UpdateToken();
            ClaudeCodeCommand = _mcp.IsRunning ? _mcp.ClaudeCodeCommand : "";
            ClaudeDesktopJson = _mcp.IsRunning ? _mcp.ClaudeDesktopJson : "";
            HasBridge = _mcp.BridgePath is not null;
            Clients.Clear();
            foreach (var c in _mcp.Clients.OrderByDescending(c => c.LastSeen)) Clients.Add(new McpClientRowViewModel(c));
            HasClients = Clients.Count > 0;
        }
        finally { _syncing = false; }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_syncing) _ = ApplyEnabledAsync(value);
    }

    /// <summary>Same as flipping the toggle, but awaitable (tests).</summary>
    public async Task SetEnabledCommandForTests(bool enabled)
    {
        _syncing = true;
        IsEnabled = enabled;
        _syncing = false;
        await ApplyEnabledAsync(enabled);
    }

    async Task ApplyEnabledAsync(bool enabled)
    {
        IsBusy = true;
        try { await _mcp.SetEnabledAsync(enabled); }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    async Task RegenerateTokenAsync()
    {
        if (!await _ui.ConfirmAsync(Loc.Instance["mcp.token.regenerate"], Loc.Instance["mcp.token.regenerateConfirm"], Loc.Instance["mcp.token.regenerate"], danger: true))
            return;
        IsBusy = true;
        try { await _mcp.RegenerateTokenAsync(); }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand] Task CopyEndpointAsync() => _ui.CopyToClipboardAsync(Endpoint);
    [RelayCommand] Task CopyTokenAsync() => _mcp.Token is { } t ? _ui.CopyToClipboardAsync(t) : Task.CompletedTask;
    [RelayCommand] Task CopyClaudeCodeAsync() => _ui.CopyToClipboardAsync(_mcp.ClaudeCodeCommand);
    [RelayCommand] Task CopyClaudeDesktopAsync() => _ui.CopyToClipboardAsync(_mcp.ClaudeDesktopJson);
    [RelayCommand] Task CopyGenericJsonAsync() => _ui.CopyToClipboardAsync(_mcp.GenericHttpJson);

    [RelayCommand]
    async Task DisconnectAsync(McpClientRowViewModel? client)
    {
        if (client is null) return;
        await _mcp.DisconnectAsync(client.Row.Id);
        Refresh();
    }
}

public sealed record AuditRowViewModel(
    string Time, string Actor, string Tool, string Host, string Decision, bool IsRejected, string Result, string Duration, string Arguments);

public sealed record AuditFilterOption(string? ActorPrefix, string LabelKey)
{
    public string Label => Loc.Instance[LabelKey];
}

/// <summary>Every MCP / AI tool call, newest first (design §3 AuditEntry). Read-only view with paging.</summary>
public sealed partial class AuditLogViewModel(IAuditLogStore store, IHostRepository hosts) : ObservableObject
{
    const int PageSize = 200;
    DateTimeOffset? _oldest;
    Dictionary<Guid, string> _hostLabels = [];

    public ObservableCollection<AuditRowViewModel> Rows { get; } = [];
    [ObservableProperty] public partial AuditRowViewModel? SelectedRow { get; set; }
    [ObservableProperty] public partial bool HasMore { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;

    public IReadOnlyList<AuditFilterOption> Filters { get; } =
    [
        new(null, "audit.filter.all"),
        new("mcp:", "audit.filter.mcp"),
        new("ai:", "audit.filter.ai"),
    ];
    [ObservableProperty] public partial AuditFilterOption? SelectedFilter { get; set; }

    partial void OnSelectedFilterChanged(AuditFilterOption? value) => _ = LoadAsync();

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        _hostLabels = (await hosts.ListAsync(ct)).ToDictionary(h => h.Id, h => h.Label);
        Rows.Clear();
        _oldest = null;
        await LoadPageAsync(ct);
    }

    [RelayCommand]
    async Task LoadMoreAsync() => await LoadPageAsync(default);

    async Task LoadPageAsync(CancellationToken ct)
    {
        var page = await store.QueryAsync(new AuditQuery { Limit = PageSize, Before = _oldest, ActorPrefix = SelectedFilter?.ActorPrefix }, ct);
        foreach (var e in page) Rows.Add(ToRow(e));
        if (page.Count > 0) _oldest = page[^1].Timestamp;
        HasMore = page.Count == PageSize;
        IsEmpty = Rows.Count == 0;
    }

    AuditRowViewModel ToRow(AuditEntry e)
    {
        var loc = Loc.Instance;
        var host = e.HostId is { } id ? _hostLabels.GetValueOrDefault(id, id.ToString("N")[..8]) : "—";
        var result = e.Error is { Length: > 0 } err ? err
            : e.ExitCode is { } code ? loc.Format("audit.exitCode", ("code", code))
            : "";
        return new AuditRowViewModel(
            e.Timestamp.ToLocalTime().ToString("G", loc.Culture),
            e.Actor,
            loc.TryGet("tool." + e.Tool) ?? e.Tool,
            host,
            loc["audit.decision." + e.Decision],
            e.Decision is not (AuditDecision.Allowed or AuditDecision.Approved),
            result,
            e.DurationMs < 1000 ? $"{e.DurationMs} ms" : $"{e.DurationMs / 1000.0:0.0} s",
            PrettyJson(e.ArgumentsJson));
    }

    static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return string.Join(", ", doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().Select(p => $"{p.Name}={(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())}")
                : [doc.RootElement.GetRawText()]);
        }
        catch (JsonException) { return json; }
    }
}
