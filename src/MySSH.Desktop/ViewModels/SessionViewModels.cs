using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Ssh;

namespace MySSH.Desktop.ViewModels;

public enum SessionState { Connecting, Connected, Ended, Lost, Failed }

/// <summary>A tab in the session workspace (design §9.4). SSH events arrive on background threads and are marshalled.</summary>
public abstract partial class SessionTabViewModel : ObservableObject, IAsyncDisposable
{
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    protected SessionTabViewModel(ISshConnector connector, IUiServices ui, Guid hostId, string hostLabel, string via)
    {
        Connector = connector;
        Ui = ui;
        HostId = hostId;
        HostLabel = hostLabel;
        Via = via;
        Title = hostLabel;
    }

    protected ISshConnector Connector { get; }
    protected IUiServices Ui { get; }
    /// <summary>Stable id for MCP / AI (list_sessions, read_terminal). Short and unguessable enough to avoid mix-ups.</summary>
    public string SessionId { get; } = "s-" + Guid.NewGuid().ToString("N")[..8];
    public Guid HostId { get; }
    public string HostLabel { get; }
    /// <summary>"bastion → prod" style chain for the status bar.</summary>
    public string Via { get; }
    public abstract SessionKind Kind { get; }

    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial SessionState State { get; set; } = SessionState.Connecting;
    [ObservableProperty] public partial string? StatusMessage { get; set; }
    /// <summary>Views stay alive while hidden (terminal scrollback must survive tab switches).</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }
    public string Icon => Kind switch { SessionKind.Terminal => "terminal", SessionKind.Sftp => "folder", _ => "application" };

    public bool IsConnecting => State == SessionState.Connecting;
    public bool IsConnected => State == SessionState.Connected;
    /// <summary>Banner with a reconnect button.</summary>
    public bool ShowBanner => State is SessionState.Ended or SessionState.Lost or SessionState.Failed;
    public bool BannerIsError => State is SessionState.Lost or SessionState.Failed;
    public string StateText => Loc.Instance["session.state." + State];

    partial void OnStateChanged(SessionState value)
    {
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(BannerIsError));
        OnPropertyChanged(nameof(StateText));
    }

    protected void OnUi(Action a)
    {
        if (_ui is null || _ui == SynchronizationContext.Current) a();
        else _ui.Post(_ => a(), null);
    }

    protected static string Describe(Exception e) => e switch
    {
        SshConnectException c => Loc.Instance.Format("ssh.err." + c.Kind, ("hop", c.HopLabel ?? ""), ("message", c.Message)),
        SshConnectionLostException => Loc.Instance.Format("ssh.err.Lost", ("message", e.Message)),
        OperationCanceledException => Loc.Instance["ssh.err.Cancelled"],
        _ => e.Message,
    };

    [RelayCommand]
    protected abstract Task ReconnectAsync();

    public abstract ValueTask DisposeAsync();
}

/// <summary>Screen access for the tool layer, implemented by the view over its TerminalView. UI thread only.</summary>
public interface ITerminalScreen
{
    string GetRecentText(int maxLines);
    bool TypeText(string text, bool pressEnter);
    /// <summary>The mouse selection, or null.</summary>
    string? GetSelectionText();
}

/// <summary>What the AI assistant offers inside a terminal tab (implemented by the AI panel).</summary>
public interface ITerminalAiActions
{
    /// <summary>Ctrl+I: one command for <paramref name="request"/>; never executed.</summary>
    Task<MySSH.Ai.AiCommandSuggester.Suggestion> SuggestCommandAsync(TerminalSessionViewModel terminal, string request, CancellationToken ct);

    /// <summary>"Explain this output" / "Analyze this error" on a selection: opens the panel and asks.</summary>
    Task AskAboutSelectionAsync(TerminalSessionViewModel terminal, string selection, bool analyzeError);
}

/// <summary>Interactive shell tab. The view attaches <see cref="Backend"/> to a TerminalView.</summary>
public sealed partial class TerminalSessionViewModel(ISshConnector connector, IUiServices ui, Guid hostId, string hostLabel, string via)
    : SessionTabViewModel(connector, ui, hostId, hostLabel, via)
{
    ISshConnection? _connection;
    CancellationTokenSource? _cts;
    int _cols = 80, _rows = 24;

    public override SessionKind Kind => SessionKind.Terminal;
    [ObservableProperty] public partial ITerminalBackend? Backend { get; set; }
    /// <summary>Set by the view while a TerminalView shows this session (UI thread only).</summary>
    public ITerminalScreen? Screen { get; set; }

    /// <summary>Set by the workspace when the AI assistant is available.</summary>
    public ITerminalAiActions? Ai { get; set; }

    // ---- Ctrl+I inline command suggestion (design §7.5): Tab inserts the command, nothing is run.
    CancellationTokenSource? _inlineCts;
    [ObservableProperty] public partial bool IsInlineOpen { get; set; }
    [ObservableProperty] public partial string InlineRequest { get; set; } = "";
    [ObservableProperty] public partial bool IsInlineBusy { get; set; }
    [ObservableProperty] public partial string? InlineSuggestion { get; set; }
    [ObservableProperty] public partial string? InlineError { get; set; }
    public bool HasInlineSuggestion => !string.IsNullOrEmpty(InlineSuggestion);
    partial void OnInlineSuggestionChanged(string? value) => OnPropertyChanged(nameof(HasInlineSuggestion));

    [RelayCommand]
    void OpenInline()
    {
        if (Ai is null) return;
        InlineError = null;
        IsInlineOpen = true;
    }

    [RelayCommand]
    void CloseInline()
    {
        _inlineCts?.Cancel();
        IsInlineOpen = false;
        IsInlineBusy = false;
        InlineSuggestion = null;
        InlineError = null;
        InlineRequest = "";
    }

    [RelayCommand]
    async Task AskInlineAsync()
    {
        if (Ai is not { } ai || InlineRequest.Trim().Length == 0 || IsInlineBusy) return;
        _inlineCts?.Cancel();
        using var cts = _inlineCts = new CancellationTokenSource();
        IsInlineBusy = true;
        InlineError = null;
        InlineSuggestion = null;
        try
        {
            var r = await ai.SuggestCommandAsync(this, InlineRequest.Trim(), cts.Token);
            if (cts.IsCancellationRequested) return;
            InlineSuggestion = r.Command;
            InlineError = r.Error ?? (r.Command is null ? Loc.Instance["ai.inline.none"] : null);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_inlineCts, cts)) { _inlineCts = null; IsInlineBusy = false; }
        }
    }

    /// <summary>Types the suggestion on the prompt line without Enter; the user reviews and runs it.</summary>
    [RelayCommand]
    void InsertInline()
    {
        if (InlineSuggestion is { Length: > 0 } cmd && Screen?.TypeText(cmd, pressEnter: false) == true) CloseInline();
    }

    /// <summary>Remote working directory reported by the shell (OSC 7), when it does; SFTP opens there.</summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>OSC 7 payload ("file://host/path", percent-encoded) or a plain path → remote absolute path.</summary>
    public void NotifyWorkingDirectory(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported)) return;
        var path = reported.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(reported, UriKind.Absolute, out var uri)
            ? Uri.UnescapeDataString(uri.AbsolutePath)
            : reported;
        if (path.StartsWith('/')) WorkingDirectory = path;
    }

    /// <summary>The workspace opens an SFTP tab for this host next to this tab.</summary>
    public event Action<TerminalSessionViewModel>? OpenSftpRequested;

    [RelayCommand]
    void OpenSftp() => OpenSftpRequested?.Invoke(this);
    public int Columns => _cols;
    public int RowCount => _rows;
    [ObservableProperty] public partial string GridSize { get; set; } = "80×24";

    public void NotifyGridSize(int cols, int rows)
    {
        (_cols, _rows) = (cols, rows);
        GridSize = $"{cols}×{rows}";
    }

    public async Task ConnectAsync(int cols, int rows)
    {
        NotifyGridSize(cols, rows);
        await CloseConnectionAsync();
        _cts = new CancellationTokenSource();
        State = SessionState.Connecting;
        StatusMessage = Loc.Instance.Format("terminal.connecting", ("host", HostLabel));
        try
        {
            var conn = await Connector.ConnectAsync(HostId, ConnectionOrigin.User, _cts.Token);
            _connection = conn;
            conn.StateChanged += OnConnectionStateChanged;
            var backend = await conn.OpenShellAsync(_cols, _rows, ct: _cts.Token);
            backend.Exited += OnExited;
            Backend = backend;
            State = SessionState.Connected;
            StatusMessage = null;
        }
        catch (Exception e) when (e is SshConnectException or SshConnectionLostException or OperationCanceledException or IOException or InvalidOperationException)
        {
            State = SessionState.Failed;
            StatusMessage = Loc.Instance.Format("terminal.connectionFailed", ("error", Describe(e)));
        }
    }

    void OnConnectionStateChanged(SshConnectionStateChange change)
    {
        if (change.Current == SshConnectionState.Lost)
            OnUi(() =>
            {
                if (State != SessionState.Connected) return;
                State = SessionState.Lost;
                StatusMessage = Loc.Instance.Format("ssh.err.Lost", ("message", change.Reason ?? ""));
            });
    }

    void OnExited(TerminalExit exit) => OnUi(() =>
    {
        if (State != SessionState.Connected) return;
        State = exit.ConnectionLost ? SessionState.Lost : SessionState.Ended;
        StatusMessage = exit.ConnectionLost
            ? Loc.Instance.Format("ssh.err.Lost", ("message", exit.Reason ?? ""))
            : Loc.Instance.Format("session.ended", ("code", exit.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—"));
    });

    protected override Task ReconnectAsync() => ConnectAsync(_cols, _rows);

    async Task CloseConnectionAsync()
    {
        if (_cts is not null) { await _cts.CancelAsync(); _cts.Dispose(); _cts = null; }
        if (Backend is { } b) { b.Exited -= OnExited; Backend = null; await b.DisposeAsync(); }
        if (_connection is { } c) { c.StateChanged -= OnConnectionStateChanged; _connection = null; await c.DisposeAsync(); }
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseConnectionAsync();
        State = SessionState.Ended;
    }
}

public sealed record SftpRowViewModel(SftpEntry Entry, string Icon, string SizeText, string ModifiedText)
{
    public string Name => Entry.Name;
    public string Permissions => Entry.Permissions;
    public bool IsDirectory => Entry.Kind == SftpEntryKind.Directory;
}

public sealed record BreadcrumbItem(string Name, string Path)
{
    /// <summary>The root "/" is its own separator: no extra "/" before the root or its direct children ("/ var / log").</summary>
    public bool ShowSeparator => Path.Count(c => c == '/') > 1;
}

public sealed partial class TransferRowViewModel(TransferItem item) : ObservableObject
{
    public TransferItem Item { get; } = item;
    public string Name => SftpPath.GetName(Item.Direction == TransferDirection.Upload ? Item.DestinationPath : Item.SourcePath);
    public string DirectionIcon => Item.Direction == TransferDirection.Upload ? "upload" : "download";
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string Detail { get; set; } = "";
    [ObservableProperty] public partial bool IsActive { get; set; } = true;

    public void Refresh()
    {
        var total = Math.Max(1, Item.TotalBytes);
        Percent = Item.State == TransferState.Completed ? 100 : 100.0 * Item.BytesTransferred / total;
        IsActive = Item.State is TransferState.Queued or TransferState.Running;
        Detail = Item.State switch
        {
            TransferState.Running => $"{SftpSessionViewModel.FormatSize(Item.BytesTransferred)} / {SftpSessionViewModel.FormatSize(Item.TotalBytes)} · {SftpSessionViewModel.FormatSize((long)Item.BytesPerSecond)}/s",
            TransferState.Failed => Loc.Instance.Format("transfer.failed", ("error", Item.Error ?? "")),
            _ => Loc.Instance["transfer.state." + Item.State],
        };
    }
}

/// <summary>SFTP browser tab: navigation, file operations, transfers and a small text editor (old-app parity).</summary>
public sealed partial class SftpSessionViewModel(ISshConnector connector, IUiServices ui, Guid hostId, string hostLabel, string via)
    : SessionTabViewModel(connector, ui, hostId, hostLabel, via)
{
    public const long MaxEditableBytes = 1024 * 1024;
    ISshConnection? _connection;
    ISftpSession? _sftp;
    TransferQueue? _transfers;

    public override SessionKind Kind => SessionKind.Sftp;
    /// <summary>Directory to open first (e.g. the terminal's current directory); home when missing or not a directory.</summary>
    public string? InitialPath { get; init; }
    public ObservableCollection<SftpRowViewModel> Entries { get; } = [];
    public ObservableCollection<BreadcrumbItem> Breadcrumb { get; } = [];
    public ObservableCollection<TransferRowViewModel> Transfers { get; } = [];
    [ObservableProperty] public partial string CurrentPath { get; set; } = "/";
    [ObservableProperty] public partial SftpRowViewModel? SelectedEntry { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool IsEmptyFolder { get; set; }

    // editor (old app: edit remote text files in place)
    [ObservableProperty] public partial string? EditorPath { get; set; }
    [ObservableProperty] public partial string EditorText { get; set; } = "";
    public bool IsEditing => EditorPath is not null;
    partial void OnEditorPathChanged(string? value) => OnPropertyChanged(nameof(IsEditing));

    public async Task ConnectAsync()
    {
        await CloseConnectionAsync();
        State = SessionState.Connecting;
        StatusMessage = Loc.Instance["sftp.connecting"];
        try
        {
            var conn = await Connector.ConnectAsync(HostId, ConnectionOrigin.User);
            _connection = conn;
            conn.StateChanged += OnConnectionStateChanged;
            _sftp = await conn.OpenSftpAsync();
            _transfers = new TransferQueue(_sftp, new TransferQueueOptions { ConflictPolicy = ConflictPolicy.Ask, OnConflict = OnConflictAsync });
            _transfers.ItemChanged += i => OnUi(() => UpdateTransfer(i));
            _transfers.ProgressChanged += p => OnUi(() => Transfers.FirstOrDefault(t => t.Item.Id == p.ItemId)?.Refresh());
            State = SessionState.Connected;
            StatusMessage = null;
            var start = _sftp.HomeDirectory;
            if (InitialPath is { Length: > 0 } initial)
            {
                try
                {
                    if (await _sftp.StatAsync(initial) is { Kind: SftpEntryKind.Directory or SftpEntryKind.Symlink })
                        start = initial;
                }
                catch (Exception e) when (e is IOException or InvalidOperationException) { }
            }
            await NavigateAsync(start);
        }
        catch (Exception e) when (e is SshConnectException or SshConnectionLostException or OperationCanceledException or IOException or InvalidOperationException)
        {
            State = SessionState.Failed;
            StatusMessage = Loc.Instance.Format("terminal.connectionFailed", ("error", Describe(e)));
        }
    }

    void OnConnectionStateChanged(SshConnectionStateChange change)
    {
        if (change.Current == SshConnectionState.Lost)
            OnUi(() =>
            {
                State = SessionState.Lost;
                StatusMessage = Loc.Instance.Format("ssh.err.Lost", ("message", change.Reason ?? ""));
            });
    }

    async ValueTask<ConflictResolution> OnConflictAsync(TransferConflict c, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        OnUi(async () => tcs.TrySetResult(await Ui.ConfirmAsync(Loc.Instance["transfer.conflictTitle"],
            Loc.Instance.Format("transfer.conflictBody", ("path", c.DestinationPath)), Loc.Instance["transfer.overwrite"], danger: true)));
        return await tcs.Task.WaitAsync(ct) ? ConflictResolution.Overwrite : ConflictResolution.Skip;
    }

    void UpdateTransfer(TransferItem item)
    {
        var row = Transfers.FirstOrDefault(t => t.Item.Id == item.Id);
        if (row is null) Transfers.Insert(0, row = new TransferRowViewModel(item));
        row.Refresh();
        if (item.State == TransferState.Completed && item.Direction == TransferDirection.Upload)
            _ = RefreshAsync();
    }

    public async Task NavigateAsync(string path)
    {
        if (_sftp is null) return;
        Error = null;
        try
        {
            var target = SftpPath.Normalize(path, CurrentPath);
            var list = await _sftp.ListAsync(target);
            CurrentPath = target;
            Entries.Clear();
            foreach (var e in list.OrderByDescending(e => e.Kind == SftpEntryKind.Directory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                Entries.Add(ToRow(e));
            IsEmptyFolder = Entries.Count == 0;
            Breadcrumb.Clear();
            var acc = "";
            Breadcrumb.Add(new BreadcrumbItem("/", "/"));
            foreach (var part in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
                Breadcrumb.Add(new BreadcrumbItem(part, acc += "/" + part));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = Loc.Instance.Format("sftp.loadFailed", ("error", e.Message));
        }
    }

    static SftpRowViewModel ToRow(SftpEntry e) => new(e,
        e.Kind switch { SftpEntryKind.Directory => "folder", SftpEntryKind.Symlink => "launch", _ => "document" },
        e.Kind == SftpEntryKind.Directory ? "—" : FormatSize(e.Size),
        e.Modified.ToLocalTime().ToString("g", Loc.Instance.Culture));

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    [RelayCommand] Task RefreshAsync() => NavigateAsync(CurrentPath);
    [RelayCommand] Task GoUpAsync() => NavigateAsync(SftpPath.GetParent(CurrentPath));
    [RelayCommand] Task NavigateToAsync(BreadcrumbItem item) => NavigateAsync(item.Path);

    [RelayCommand]
    async Task OpenAsync(SftpRowViewModel? row)
    {
        row ??= SelectedEntry;
        if (row is null) return;
        if (row.IsDirectory || (row.Entry.Kind == SftpEntryKind.Symlink && row.Entry.LinkTarget is { } t && await IsDirectoryAsync(t)))
            await NavigateAsync(row.Entry.Kind == SftpEntryKind.Symlink ? row.Entry.LinkTarget! : row.Entry.FullPath);
        else
            await EditAsync(row);
    }

    async Task<bool> IsDirectoryAsync(string path) => _sftp is not null && (await _sftp.StatAsync(path))?.Kind == SftpEntryKind.Directory;

    async Task<string?> RunAsync(Func<Task> op, string errorKey)
    {
        Error = null;
        try { await op(); return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            return Error = Loc.Instance.Format(errorKey, ("error", e.Message));
        }
    }

    [RelayCommand]
    async Task NewFolderAsync()
    {
        if (_sftp is null || await Ui.PromptTextAsync(Loc.Instance["sftp.newFolder"], Loc.Instance["sftp.folderName"], "") is not { } name) return;
        if (await RunAsync(() => _sftp.CreateDirectoryAsync(SftpPath.Combine(CurrentPath, name)), "sftp.createFailed") is null) await RefreshAsync();
    }

    [RelayCommand]
    async Task NewFileAsync()
    {
        if (_sftp is null || await Ui.PromptTextAsync(Loc.Instance["sftp.newFile"], Loc.Instance["sftp.fileName"], "") is not { } name) return;
        if (await RunAsync(() => _sftp.WriteAllTextAsync(SftpPath.Combine(CurrentPath, name), ""), "sftp.createFileFailed") is null) await RefreshAsync();
    }

    [RelayCommand]
    async Task RenameAsync(SftpRowViewModel? row)
    {
        row ??= SelectedEntry;
        if (_sftp is null || row is null || await Ui.PromptTextAsync(Loc.Instance["sftp.rename"], Loc.Instance["sftp.newName"], row.Name) is not { } name || name == row.Name) return;
        if (await RunAsync(() => _sftp.RenameAsync(row.Entry, SftpPath.Combine(CurrentPath, name)), "sftp.renameFailed") is null) await RefreshAsync();
    }

    [RelayCommand]
    async Task DeleteAsync(SftpRowViewModel? row)
    {
        row ??= SelectedEntry;
        if (_sftp is null || row is null) return;
        var key = row.IsDirectory ? "sftp.deleteFolderConfirm" : row.Entry.Kind == SftpEntryKind.Symlink ? "sftp.deleteLinkConfirm" : "sftp.deleteConfirm";
        if (!await Ui.ConfirmAsync(Loc.Instance["sftp.delete"], Loc.Instance.Format(key, ("name", row.Name)), Loc.Instance["common.delete"], danger: true)) return;
        // DeleteAsync never follows symlinks (P0 finding Q7g) and recursion never descends into them.
        if (await RunAsync(() => _sftp.DeleteAsync(row.Entry, recursive: row.IsDirectory), "sftp.deleteFailed") is null) await RefreshAsync();
    }

    [RelayCommand]
    async Task UploadAsync()
    {
        if (_transfers is null || await Ui.PickOpenFileAsync(Loc.Instance["sftp.selectFile"]) is not { } local) return;
        _transfers.EnqueueUpload(local, SftpPath.Combine(CurrentPath, Path.GetFileName(local)));
    }

    /// <summary>Drag &amp; drop: files and folders from the desktop.</summary>
    public void UploadPaths(IEnumerable<string> localPaths)
    {
        if (_transfers is null) return;
        foreach (var p in localPaths)
            _transfers.EnqueueUpload(p, SftpPath.Combine(CurrentPath, Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar))));
    }

    [RelayCommand]
    async Task DownloadAsync(SftpRowViewModel? row)
    {
        row ??= SelectedEntry;
        if (_transfers is null || row is null) return;
        string? local = row.IsDirectory
            ? await Ui.PickFolderAsync(Loc.Instance["sftp.saveAs"]) is { } dir ? Path.Combine(dir, row.Name) : null
            : await Ui.PickSaveFileAsync(Loc.Instance["sftp.saveAs"], row.Name);
        if (local is not null) _transfers.EnqueueDownload(row.Entry.FullPath, local);
    }

    [RelayCommand]
    void CancelTransfer(TransferRowViewModel row) => _transfers?.Cancel(row.Item.Id);

    [RelayCommand]
    void ClearFinishedTransfers()
    {
        foreach (var t in Transfers.Where(t => !t.IsActive).ToList()) Transfers.Remove(t);
    }

    async Task EditAsync(SftpRowViewModel row)
    {
        if (_sftp is null) return;
        if (row.Entry.Size > MaxEditableBytes) { Error = Loc.Instance["sftp.tooLargeToEdit"]; return; }
        string? text = null;
        if (await RunAsync(async () => text = await _sftp.ReadAllTextAsync(row.Entry.FullPath, MaxEditableBytes), "sftp.readFailed") is not null) return;
        if (text!.Contains('\0', StringComparison.Ordinal)) { Error = Loc.Instance["sftp.binaryFile"]; return; }
        EditorText = text;
        EditorPath = row.Entry.FullPath;
    }

    [RelayCommand]
    async Task SaveEditorAsync()
    {
        if (_sftp is null || EditorPath is null) return;
        if (await RunAsync(() => _sftp.WriteAllTextAsync(EditorPath, EditorText), "sftp.saveFailed") is null)
        {
            EditorPath = null;
            EditorText = "";
            await RefreshAsync();
        }
    }

    [RelayCommand]
    void CloseEditor()
    {
        EditorPath = null;
        EditorText = "";
    }

    protected override Task ReconnectAsync() => ConnectAsync();

    async Task CloseConnectionAsync()
    {
        if (_transfers is { } q) { _transfers = null; await q.DisposeAsync(); }
        if (_sftp is { } s) { _sftp = null; await s.DisposeAsync(); }
        if (_connection is { } c) { c.StateChanged -= OnConnectionStateChanged; _connection = null; await c.DisposeAsync(); }
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseConnectionAsync();
        State = SessionState.Ended;
    }
}

/// <summary>The "Sessions" page: open tabs and the active one.</summary>
public sealed partial class SessionsWorkspaceViewModel(Func<ISshConnector?> connector, IUiServices ui) : ObservableObject, IAsyncDisposable
{
    /// <summary>Needed for RDP tabs (host / identity lookup, client launcher); set after unlock.</summary>
    public RdpServices? Rdp { get; set; }

    /// <summary>AI actions for terminal tabs (Ctrl+I, ask about a selection); null when the assistant is not set up.</summary>
    public ITerminalAiActions? TerminalAi
    {
        get;
        set
        {
            field = value;
            foreach (var t in Tabs.OfType<TerminalSessionViewModel>()) t.Ai = value;
        }
    }

    public ObservableCollection<SessionTabViewModel> Tabs { get; } = [];
    [ObservableProperty] public partial SessionTabViewModel? SelectedTab { get; set; }
    public bool IsEmpty => Tabs.Count == 0;
    public bool HasTabs => Tabs.Count > 0;

    partial void OnSelectedTabChanged(SessionTabViewModel? oldValue, SessionTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    /// <summary>Terminal tabs connect once their view knows the grid size; SFTP tabs connect immediately.</summary>
    public SessionTabViewModel Open(HostRowViewModel host, SessionKind kind)
    {
        var c = connector() ?? throw new InvalidOperationException("Not unlocked.");
        SessionTabViewModel tab = kind switch
        {
            SessionKind.Terminal => new TerminalSessionViewModel(c, ui, host.Id, host.Name, host.Via),
            SessionKind.Sftp => new SftpSessionViewModel(c, ui, host.Id, host.Name, host.Via) { Title = $"{host.Name} · SFTP" },
            _ => new RdpSessionViewModel(c, ui, host.Id, host.Name, host.Via, Rdp ?? throw new InvalidOperationException("RDP services are not set.")),
        };
        Tabs.Add(tab);
        SelectedTab = tab;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasTabs));
        if (tab is SftpSessionViewModel sftp) _ = sftp.ConnectAsync();
        if (tab is RdpSessionViewModel rdp) _ = rdp.ConnectAsync();
        if (tab is TerminalSessionViewModel term)
        {
            term.OpenSftpRequested += t => OpenSftpFor(t);
            term.Ai = TerminalAi;
        }
        return tab;
    }

    /// <summary>SFTP for the terminal's host, inserted right after it, opened in the terminal's working directory.</summary>
    public SftpSessionViewModel OpenSftpFor(TerminalSessionViewModel terminal)
    {
        var c = connector() ?? throw new InvalidOperationException("Not unlocked.");
        var sftp = new SftpSessionViewModel(c, ui, terminal.HostId, terminal.HostLabel, terminal.Via)
        {
            Title = $"{terminal.HostLabel} · SFTP",
            InitialPath = terminal.WorkingDirectory,
        };
        var i = Tabs.IndexOf(terminal);
        Tabs.Insert(i < 0 ? Tabs.Count : i + 1, sftp);
        SelectedTab = sftp;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasTabs));
        _ = sftp.ConnectAsync();
        return sftp;
    }

    [RelayCommand]
    async Task CloseTabAsync(SessionTabViewModel tab)
    {
        var i = Tabs.IndexOf(tab);
        if (i < 0) return;
        Tabs.RemoveAt(i);
        if (SelectedTab == tab || SelectedTab is null) SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(i, Tabs.Count - 1)];
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasTabs));
        await tab.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var t in Tabs.ToList()) await t.DisposeAsync();
        Tabs.Clear();
    }
}
