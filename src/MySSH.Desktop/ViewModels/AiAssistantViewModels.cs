using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySSH.Ai;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Tools;

namespace MySSH.Desktop.ViewModels;

/// <summary>What the assistant can see of the user's workspace (the selected terminal tab).</summary>
public interface IAiWorkspace
{
    TerminalSessionViewModel? CurrentTerminal { get; }
}

/// <summary>Right-hand AI panel (design §7.5): conversation list, the open chat, provider choice.</summary>
public sealed partial class AiAssistantViewModel : ObservableObject, ITerminalAiActions
{
    readonly AiSettingsService _settings;
    readonly IAiConversationStore _store;
    readonly IHostRepository _hosts;
    readonly IToolInvoker _tools;
    readonly IToolCatalog _catalog;
    readonly IUiServices _ui;
    readonly IAiWorkspace _workspace;

    public AiAssistantViewModel(AiSettingsService settings, IAiConversationStore store, IHostRepository hosts, IToolInvoker tools,
        IToolCatalog catalog, IUiServices ui, IAiWorkspace workspace)
    {
        _settings = settings;
        _store = store;
        _hosts = hosts;
        _tools = tools;
        _catalog = catalog;
        _ui = ui;
        _workspace = workspace;
        _settings.Changed += () => Post(RefreshProviders);
        RefreshProviders();
        Current = NewChatViewModel(null);
    }

    readonly SynchronizationContext? _sync = SynchronizationContext.Current;
    void Post(Action a)
    {
        if (_sync is null || _sync == SynchronizationContext.Current) a();
        else _sync.Post(_ => a(), null);
    }

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial AiChatViewModel Current { get; set; }
    [ObservableProperty] public partial bool IsHistoryOpen { get; set; }
    public ObservableCollection<AiConversationItemViewModel> Conversations { get; } = [];
    public ObservableCollection<AiProviderConfig> Providers { get; } = [];
    [ObservableProperty] public partial AiProviderConfig? SelectedProvider { get; set; }
    public bool HasProvider => Providers.Count > 0;
    public bool NeedsSetup => Providers.Count == 0;

    /// <summary>The shell navigates to AI / MCP → AI assistant.</summary>
    public event Action? OpenSettingsRequested;

    internal AiSettings Settings => _settings.Current;
    internal AiSettingsService SettingsService => _settings;
    internal IAiConversationStore Store => _store;
    internal IHostRepository Hosts => _hosts;
    internal IToolInvoker Tools => _tools;
    internal IToolCatalog Catalog => _catalog;
    internal IUiServices Ui => _ui;
    internal IAiWorkspace Workspace => _workspace;

    void RefreshProviders()
    {
        var keep = SelectedProvider?.Id;
        Providers.Clear();
        foreach (var p in _settings.Current.Providers) Providers.Add(p);
        SelectedProvider = Providers.FirstOrDefault(p => p.Id == keep) ?? _settings.Current.DefaultProvider;
        OnPropertyChanged(nameof(HasProvider));
        OnPropertyChanged(nameof(NeedsSetup));
    }

    AiChatViewModel NewChatViewModel(AiConversation? conversation) => new(this, conversation);

    [RelayCommand]
    void Toggle()
    {
        IsOpen = !IsOpen;
        if (IsOpen) _ = LoadConversationsAsync();
    }

    [RelayCommand]
    void Close() => IsOpen = false;

    [RelayCommand]
    void OpenSettings() => OpenSettingsRequested?.Invoke();

    /// <summary>A code block's "Insert into terminal": typed on the prompt line of the selected terminal, never run.</summary>
    [RelayCommand]
    void InsertCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        if (_workspace.CurrentTerminal is not { Screen: { } screen, IsConnected: true })
        {
            Current.AddNotice(AiNoticeKind.Warning, Loc.Instance["ai.code.noTerminal"]);
            return;
        }
        if (!screen.TypeText(code.Trim(), pressEnter: false))
            Current.AddNotice(AiNoticeKind.Warning, Loc.Instance["ai.code.multiline"]);
    }

    [RelayCommand]
    void NewChat()
    {
        if (Current.IsRunning) Current.StopCommand.Execute(null);
        Current = NewChatViewModel(null);
        IsHistoryOpen = false;
    }

    [RelayCommand]
    async Task ToggleHistoryAsync()
    {
        IsHistoryOpen = !IsHistoryOpen;
        if (IsHistoryOpen) await LoadConversationsAsync();
    }

    public async Task LoadConversationsAsync(CancellationToken ct = default)
    {
        if (_settings.Current.RetentionDays is int days && days > 0)
            await _store.PruneAsync(DateTimeOffset.UtcNow.AddDays(-days), ct);
        var list = await _store.ListAsync(ct);
        Conversations.Clear();
        foreach (var c in list) Conversations.Add(new AiConversationItemViewModel(c));
    }

    [RelayCommand]
    async Task OpenConversationAsync(AiConversationItemViewModel? item)
    {
        if (item is null) return;
        if (Current.IsRunning) Current.StopCommand.Execute(null);
        var chat = NewChatViewModel(item.Conversation);
        Current = chat;
        IsHistoryOpen = false;
        await chat.LoadAsync();
    }

    [RelayCommand]
    async Task DeleteConversationAsync(AiConversationItemViewModel? item)
    {
        if (item is null) return;
        if (!await _ui.ConfirmAsync(Loc.Instance["ai.deleteChat.title"], Loc.Instance.Format("ai.deleteChat.body", ("title", item.Title)),
                Loc.Instance["common.delete"], danger: true)) return;
        await _store.DeleteAsync(item.Conversation.Id);
        Conversations.Remove(item);
        if (Current.ConversationId == item.Conversation.Id) Current = NewChatViewModel(null);
    }

    /// <summary>Opens the panel with a fresh chat, the given context attached and (optionally) the question sent.</summary>
    public async Task AskAsync(string question, AiContextItem? extraContext, bool send)
    {
        IsOpen = true;
        if (Current.IsRunning || Current.HasMessages) Current = NewChatViewModel(null);
        if (extraContext is not null) Current.AddChip(extraContext);
        Current.Input = question;
        if (send) await Current.SendCommand.ExecuteAsync(null);
    }

    /// <summary>First use of a provider: "what you send goes to {provider}" (design §7.7). False when the user declines.</summary>
    internal async Task<bool> EnsureDataNoticeAsync(AiProviderConfig provider)
    {
        if (_settings.Current.DataNoticeAcknowledged.Contains(provider.Id)) return true;
        if (!await _ui.ConfirmAsync(Loc.Instance.Format("ai.dataNotice.title", ("provider", provider.Name)),
                Loc.Instance.Format("ai.dataNotice.body", ("provider", provider.Name)), Loc.Instance["ai.dataNotice.accept"], danger: false))
            return false;
        var latest = _settings.Current;
        await _settings.SaveAsync(latest with { DataNoticeAcknowledged = [.. latest.DataNoticeAcknowledged, provider.Id] });
        return true;
    }

    /// <summary>
    /// Host metadata of a terminal tab as a context item (null when the host is gone). The chip shows the host's AI policy
    /// and the model is told it, so with "Off" it explains instead of trying tools that can't see the host.
    /// </summary>
    internal async Task<(AiContextItem Item, Host Host)?> HostContextAsync(TerminalSessionViewModel term)
    {
        var host = await _hosts.GetAsync(term.HostId);
        if (host is null) return null;
        var policy = host.AiPolicy == AiPolicy.Off
            ? "ai_policy: Off. MySSH hides this host from your tools (list_hosts, run_command, read_terminal, sftp_*) until the user " +
              "enables AI access for it. Don't call tools for it; answer from the context, and if you need access say so in one sentence " +
              "(the user can turn it on from the card shown in the chat)."
            : "ai_policy: " + host.AiPolicy;
        var label = host.AiPolicy == AiPolicy.Off
            ? Loc.Instance.Format("ai.chip.hostOff", ("host", host.Label))
            : Loc.Instance.Format("ai.chip.host", ("host", host.Label), ("policy", Loc.Instance["aiPolicy." + host.AiPolicy]));
        var item = new AiContextItem(AiContextKind.Host, label,
            AiContextComposer.DescribeHost(host.Label, host.Hostname, host.Port, host.Username, term.WorkingDirectory, host.Id) +
            "\nterminal_session: " + term.SessionId + "\n" + policy);
        return (item, host);
    }

    /// <summary>The host list (and anything showing host policies) should reload.</summary>
    public event Action? HostsChanged;

    /// <summary>The user turned AI access on for a host from the chat card (never the model).</summary>
    internal async Task<bool> SetHostPolicyAsync(Guid hostId, AiPolicy policy)
    {
        if (await _hosts.GetAsync(hostId) is not { } host) return false;
        await _hosts.SaveAsync(host with { AiPolicy = policy, UpdatedAt = DateTimeOffset.UtcNow });
        HostsChanged?.Invoke();
        return true;
    }

    public async Task<AiCommandSuggester.Suggestion> SuggestCommandAsync(TerminalSessionViewModel terminal, string request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var provider = SelectedProvider ?? _settings.Current.DefaultProvider;
        if (provider is null) return new(null, Loc.Instance["ai.noProvider"]);
        if (string.IsNullOrWhiteSpace(provider.Model)) return new(null, Loc.Instance.Format("ai.noModel", ("provider", provider.Name)));
        if (!await EnsureDataNoticeAsync(provider)) return new(null, Loc.Instance["ai.inline.declined"]);
        var context = new List<AiContextItem>();
        if (await HostContextAsync(terminal) is { } host) context.Add(host.Item);
        if (terminal.Screen is { } screen)
            context.Add(new AiContextItem(AiContextKind.Terminal, terminal.Title, screen.GetRecentText(40)));
        var composed = AiContextComposer.Compose(request, context, _settings.Current.RedactSecrets);
        var chat = await _settings.CreateProviderAsync(provider, ct);
        var r = await AiCommandSuggester.SuggestAsync(chat, provider.Model, composed.Text, ct);
        return r.Error is null ? r : r with { Error = Loc.Instance.Format("ai.error." + r.ErrorKind, ("detail", r.Error)) };
    }

    public Task AskAboutSelectionAsync(TerminalSessionViewModel terminal, string selection, bool analyzeError)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return AskAsync(Loc.Instance[analyzeError ? "ai.ask.analyzeError" : "ai.ask.explain"],
            new AiContextItem(AiContextKind.Selection, Loc.Instance.Format("ai.chip.selection", ("title", terminal.Title)), selection), send: true);
    }

    /// <summary>Approval from the tool layer for an AI call; null when no open chat owns it (the caller falls back to the modal).</summary>
    public Task<ApprovalOutcome>? TryRequestApproval(ApprovalRequest request, AiToolCallInfo info, CancellationToken ct) =>
        Current.ConversationId == info.ConversationId && Current.IsRunning ? Current.RequestApprovalAsync(request, info, ct) : null;
}

public sealed class AiConversationItemViewModel(AiConversation conversation)
{
    public AiConversation Conversation { get; } = conversation;
    public string Title => Conversation.Title.Length > 0 ? Conversation.Title : Loc.Instance["ai.untitled"];
    public string When => Conversation.UpdatedAt.ToLocalTime().ToString("g", Loc.Instance.Culture);
}

public sealed partial class AiContextChipViewModel(AiContextItem item, Action<AiContextChipViewModel> remove) : ObservableObject
{
    public AiContextItem Item { get; } = item;
    public string Label => Item.Label;
    public string Icon => Item.Kind switch
    {
        AiContextKind.Host => "server",
        AiContextKind.Terminal => "terminal",
        AiContextKind.File => "document",
        _ => "text-font",
    };

    [RelayCommand]
    void Remove() => remove(this);
}

// ------------------------------------------------------------------ chat items

public abstract partial class AiChatItemViewModel : ObservableObject;

public sealed class AiUserItemViewModel(string text, IReadOnlyList<string> contextLabels) : AiChatItemViewModel
{
    public string Text { get; } = text;
    public string ContextText { get; } = string.Join(" · ", contextLabels);
    public bool HasContext => ContextText.Length > 0;
}

public sealed partial class AiAssistantItemViewModel : AiChatItemViewModel
{
    readonly StringBuilder _text = new();
    readonly StringBuilder _thinking = new();
    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial string Thinking { get; set; } = "";
    [ObservableProperty] public partial bool IsStreaming { get; set; }
    [ObservableProperty] public partial bool IsThinkingExpanded { get; set; }
    public bool HasText => Text.Length > 0;
    public bool HasThinking => Thinking.Length > 0;
    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(HasText));
    partial void OnThinkingChanged(string value) => OnPropertyChanged(nameof(HasThinking));

    public void AppendText(string delta) { _text.Append(delta); Text = _text.ToString(); }
    public void AppendThinking(string delta) { _thinking.Append(delta); Thinking = _thinking.ToString(); }

    [RelayCommand]
    void ToggleThinking() => IsThinkingExpanded = !IsThinkingExpanded;
}

public enum AiToolCardState { Running, AwaitingApproval, Succeeded, Failed, Denied }

public sealed partial class AiToolCardViewModel : AiChatItemViewModel
{
    TaskCompletionSource<ApprovalOutcome>? _approval;
    readonly Action<ApprovalRequest>? _allowForSession;
    ApprovalRequest? _request;

    public AiToolCardViewModel(ToolCall call, string title, string summary, Action<ApprovalRequest>? allowForSession = null)
    {
        Call = call;
        Title = title;
        Summary = summary;
        _allowForSession = allowForSession;
    }

    public ToolCall Call { get; }
    public string Title { get; }
    [ObservableProperty] public partial string Summary { get; set; }
    [ObservableProperty] public partial string? HostLabel { get; set; }
    [ObservableProperty] public partial AiToolCardState State { get; set; } = AiToolCardState.Running;
    [ObservableProperty] public partial string Output { get; set; } = "";
    [ObservableProperty] public partial string? ApprovalReason { get; set; }
    [ObservableProperty] public partial bool IsHighRisk { get; set; }
    [ObservableProperty] public partial bool IsOutputExpanded { get; set; }

    public bool IsAwaitingApproval => State == AiToolCardState.AwaitingApproval;
    public bool IsRunning => State == AiToolCardState.Running;
    public bool HasOutput => Output.Length > 0 && State != AiToolCardState.AwaitingApproval;
    public string StateText => State switch
    {
        AiToolCardState.Running => Loc.Instance["ai.tool.running"],
        AiToolCardState.AwaitingApproval => Loc.Instance["ai.tool.awaiting"],
        AiToolCardState.Succeeded => Loc.Instance["ai.tool.done"],
        AiToolCardState.Denied => Loc.Instance["ai.tool.denied"],
        _ => Loc.Instance["ai.tool.failed"],
    };
    public MySSH.UI.Carbon.TagType StateTag => State switch
    {
        AiToolCardState.Succeeded => MySSH.UI.Carbon.TagType.Green,
        AiToolCardState.AwaitingApproval => IsHighRisk ? MySSH.UI.Carbon.TagType.Red : MySSH.UI.Carbon.TagType.Blue,
        AiToolCardState.Running => MySSH.UI.Carbon.TagType.Gray,
        _ => MySSH.UI.Carbon.TagType.Red,
    };

    partial void OnStateChanged(AiToolCardState value)
    {
        OnPropertyChanged(nameof(IsAwaitingApproval));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateTag));
    }
    partial void OnOutputChanged(string value) => OnPropertyChanged(nameof(HasOutput));
    partial void OnIsHighRiskChanged(bool value) => OnPropertyChanged(nameof(StateTag));

    internal Task<ApprovalOutcome> AwaitApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        _request = request;
        Summary = request.Summary;
        HostLabel = $"{request.Host.Label} ({request.Host.Hostname})";
        ApprovalReason = MainWindow.LocalizedReason(request);
        IsHighRisk = request.HighRisk;
        State = AiToolCardState.AwaitingApproval;
        _approval = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs = _approval;
        var reg = ct.Register(() => tcs.TrySetResult(ApprovalOutcome.Cancelled));
        _ = tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
        return tcs.Task;
    }

    void Answer(ApprovalOutcome outcome)
    {
        if (_approval is not { } tcs) return;
        _approval = null;
        State = outcome == ApprovalOutcome.Approved ? AiToolCardState.Running : AiToolCardState.Denied;
        tcs.TrySetResult(outcome);
    }

    [RelayCommand]
    void Allow() => Answer(ApprovalOutcome.Approved);

    [RelayCommand]
    void Deny() => Answer(ApprovalOutcome.Denied);

    [RelayCommand]
    void AllowForSession()
    {
        if (_request is { } r) _allowForSession?.Invoke(r);
        Answer(ApprovalOutcome.Approved);
    }

    internal void ApprovalEnded()
    {
        // Deadline or cancellation while the card still waited.
        if (_approval is { } tcs && tcs.Task.IsCompleted) { _approval = null; State = AiToolCardState.Denied; }
    }

    [RelayCommand]
    void ToggleOutput() => IsOutputExpanded = !IsOutputExpanded;

    public void Finish(ToolResult result)
    {
        _approval?.TrySetResult(ApprovalOutcome.Cancelled);
        _approval = null;
        Output = result.Content;
        State = !result.IsError ? AiToolCardState.Succeeded
            : State == AiToolCardState.Denied || result.Content.Contains("denied", StringComparison.OrdinalIgnoreCase) ? AiToolCardState.Denied
            : AiToolCardState.Failed;
    }
}

/// <summary>"AI access is off for this host" with one-click enabling (the user's choice; the model can't change policies).</summary>
public sealed partial class AiPolicyCardViewModel(Guid hostId, string hostLabel, Func<AiPolicy, Task<bool>> enable) : AiChatItemViewModel
{
    public Guid HostId { get; } = hostId;
    public string Text { get; } = Loc.Instance.Format("ai.policyCard.body", ("host", hostLabel));
    [ObservableProperty] public partial bool IsDone { get; set; }
    [ObservableProperty] public partial string? DoneText { get; set; }
    public bool IsPending => !IsDone;
    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(IsPending));

    async Task SetAsync(AiPolicy policy)
    {
        if (IsDone || !await enable(policy)) return;
        DoneText = Loc.Instance.Format("ai.policyCard.done", ("policy", Loc.Instance["aiPolicy." + policy]));
        IsDone = true;
    }

    [RelayCommand]
    Task EnableReadOnlyAsync() => SetAsync(AiPolicy.ReadOnly);

    [RelayCommand]
    Task EnableAskAsync() => SetAsync(AiPolicy.Ask);
}

public enum AiNoticeKind { Info, Warning, Error }

public sealed class AiNoticeItemViewModel(AiNoticeKind kind, string text) : AiChatItemViewModel
{
    public AiNoticeKind Kind { get; } = kind;
    public string Text { get; } = text;
    public bool IsError => Kind == AiNoticeKind.Error;
    public bool IsWarning => Kind == AiNoticeKind.Warning;
}

// ------------------------------------------------------------------ one conversation

public sealed partial class AiChatViewModel : ObservableObject, IDisposable
{
    readonly AiAssistantViewModel _owner;
    AiConversation? _conversation;
    readonly List<AiMessage> _history = [];
    CancellationTokenSource? _run;
    readonly HashSet<string> _sessionApprovals = new(StringComparer.Ordinal);
    readonly Dictionary<string, AiToolCardViewModel> _cards = new(StringComparer.Ordinal);
    AiAssistantItemViewModel? _streaming;

    internal AiChatViewModel(AiAssistantViewModel owner, AiConversation? conversation)
    {
        _owner = owner;
        _conversation = conversation;
        ConversationId = conversation?.Id ?? Guid.NewGuid();
        Title = conversation?.Title ?? "";
        if (owner.Settings.AttachHostByDefault) _ = AttachCurrentHostAsync(silent: true);
    }

    public Guid ConversationId { get; }
    public ObservableCollection<AiChatItemViewModel> Items { get; } = [];
    public ObservableCollection<AiContextChipViewModel> Chips { get; } = [];
    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string Input { get; set; } = "";
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string UsageText { get; set; } = "";
    public bool HasMessages => Items.Count > 0;
    public bool HasChips => Chips.Count > 0;
    public bool CanSend => !IsRunning && Input.Trim().Length > 0;
    partial void OnInputChanged(string value) => OnPropertyChanged(nameof(CanSend));
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanSend));

    /// <summary>History as sent to the model (for tests).</summary>
    internal IReadOnlyList<AiMessage> History => _history;

    long _inputTokens, _outputTokens;

    internal void AddChip(AiContextItem item)
    {
        var existing = Chips.FirstOrDefault(c => c.Item.Kind == item.Kind && c.Item.Label == item.Label);
        if (existing is not null) Chips.Remove(existing);
        Chips.Add(new AiContextChipViewModel(item, c => { Chips.Remove(c); OnPropertyChanged(nameof(HasChips)); }));
        OnPropertyChanged(nameof(HasChips));
    }

    async Task AttachCurrentHostAsync(bool silent)
    {
        if (_owner.Workspace.CurrentTerminal is not { } term) { if (!silent) AddNotice(AiNoticeKind.Warning, Loc.Instance["ai.context.noTerminal"]); return; }
        if (await _owner.HostContextAsync(term) is not { } ctx)
        {
            if (!silent) AddNotice(AiNoticeKind.Warning, Loc.Instance["ai.context.noHost"]);
            return;
        }
        // One host chip at a time: replace a previous one (also refreshes the policy after it changed).
        foreach (var old in Chips.Where(c => c.Item.Kind == AiContextKind.Host).ToList()) Chips.Remove(old);
        AddChip(ctx.Item);
        if (ctx.Host.AiPolicy == AiPolicy.Off && !Items.OfType<AiPolicyCardViewModel>().Any(c => c.HostId == ctx.Host.Id && !c.IsDone))
        {
            Items.Add(new AiPolicyCardViewModel(ctx.Host.Id, ctx.Host.Label, async policy =>
            {
                if (!await _owner.SetHostPolicyAsync(ctx.Host.Id, policy)) return false;
                await AttachCurrentHostAsync(silent: true);
                return true;
            }));
            OnPropertyChanged(nameof(HasMessages));
        }
    }

    [RelayCommand]
    Task AttachHostAsync() => AttachCurrentHostAsync(silent: false);

    [RelayCommand]
    void AttachTerminal()
    {
        if (_owner.Workspace.CurrentTerminal is not { Screen: { } screen } term)
        {
            AddNotice(AiNoticeKind.Warning, Loc.Instance["ai.context.noTerminal"]);
            return;
        }
        var lines = Math.Clamp(_owner.Settings.TerminalContextLines, 1, 2000);
        AddChip(new AiContextItem(AiContextKind.Terminal, Loc.Instance.Format("ai.chip.terminal", ("title", term.Title), ("lines", lines)),
            screen.GetRecentText(lines)));
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_conversation is null) return;
        IReadOnlyList<AiStoredMessage> stored;
        try
        {
            stored = await _owner.Store.GetMessagesAsync(_conversation.Id, ct);
        }
        catch (AiConversationUnreadableException)
        {
            AddNotice(AiNoticeKind.Error, Loc.Instance["ai.chat.unreadable"]);
            return;
        }
        _inputTokens = _conversation.InputTokens;
        _outputTokens = _conversation.OutputTokens;
        UpdateUsage();
        foreach (var s in stored)
        {
            AiMessage m;
            try { m = AiHistorySerializer.Deserialize(s.PayloadJson); }
            catch (JsonException) { AddNotice(AiNoticeKind.Error, Loc.Instance["ai.chat.unreadable"]); _history.Clear(); return; }
            _history.Add(m);
            Render(m);
        }
    }

    void Render(AiMessage m)
    {
        switch (m.Role)
        {
            case AiRole.User:
                var (question, labels) = SplitContext(m.Text ?? "");
                Items.Add(new AiUserItemViewModel(question, labels));
                break;
            case AiRole.Assistant:
                var item = new AiAssistantItemViewModel();
                if (m.Thinking is { Length: > 0 } th) item.AppendThinking(th);
                if (m.Text is { Length: > 0 } t) item.AppendText(t);
                if (item.HasText || item.HasThinking) Items.Add(item);
                foreach (var call in m.ToolCalls) Items.Add(_cards[call.Id] = NewCard(call));
                break;
            case AiRole.ToolResults:
                foreach (var r in m.ToolResults)
                    if (_cards.TryGetValue(r.ToolCallId, out var card)) card.Finish(r);
                break;
        }
        OnPropertyChanged(nameof(HasMessages));
    }

    /// <summary>The stored user text starts with the &lt;context&gt; block; show the question and the chip labels.</summary>
    internal static (string Question, IReadOnlyList<string> Labels) SplitContext(string text)
    {
        if (!text.StartsWith("<context>", StringComparison.Ordinal)) return (text, []);
        var end = text.IndexOf("</context>", StringComparison.Ordinal);
        if (end < 0) return (text, []);
        var labels = new List<string>();
        var block = text[..end];
        var i = 0;
        while ((i = block.IndexOf(" label=\"", i, StringComparison.Ordinal)) >= 0)
        {
            var start = i + 8;
            var stop = block.IndexOf('"', start);
            if (stop < 0) break;
            labels.Add(block[start..stop]);
            i = stop;
        }
        return (text[(end + "</context>".Length)..].TrimStart('\n'), labels);
    }

    AiToolCardViewModel NewCard(ToolCall call) =>
        new(call, MainWindow.LocalizedToolTitle(_owner.Catalog.Find(call.Name) ?? new ToolDefinition(call.Name, call.Name, "", default, ToolCapability.Read, true, false, true, false)),
            SummarizeCall(call), r => _sessionApprovals.Add(SessionKey(r)));

    static string SessionKey(ApprovalRequest r) => r.Tool.Name + "\n" + r.Host.Id.ToString("D") + "\n" + r.Summary;

    /// <summary>One line for the card: the command, the path, the session — not the raw JSON.</summary>
    internal static string SummarizeCall(ToolCall call)
    {
        if (call.InputError is not null || call.Input is not { ValueKind: JsonValueKind.Object } input) return call.RawInput;
        foreach (var key in new[] { "command", "path", "text", "session" })
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString()!;
        return input.GetRawText();
    }

    internal void AddNotice(AiNoticeKind kind, string text)
    {
        Items.Add(new AiNoticeItemViewModel(kind, text));
        OnPropertyChanged(nameof(HasMessages));
    }

    void UpdateUsage() => UsageText = _inputTokens + _outputTokens == 0 ? "" :
        Loc.Instance.Format("ai.usage", ("input", _inputTokens.ToString("N0", CultureInfo.CurrentCulture)), ("output", _outputTokens.ToString("N0", CultureInfo.CurrentCulture)));

    [RelayCommand]
    void Stop() => _run?.Cancel();

    /// <summary>Cancels a running turn (the chat was closed or replaced).</summary>
    public void Dispose() => _run?.Cancel();

    [RelayCommand]
    async Task SendAsync()
    {
        var question = Input.Trim();
        if (question.Length == 0 || IsRunning) return;
        var settings = _owner.Settings;
        var providerConfig = _owner.SelectedProvider ?? settings.DefaultProvider;
        if (providerConfig is null)
        {
            AddNotice(AiNoticeKind.Warning, Loc.Instance["ai.noProvider"]);
            return;
        }
        var model = providerConfig.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            AddNotice(AiNoticeKind.Warning, Loc.Instance.Format("ai.noModel", ("provider", providerConfig.Name)));
            return;
        }
        if (!await _owner.EnsureDataNoticeAsync(providerConfig)) return;

        var context = Chips.Select(c => c.Item).ToList();
        var composed = AiContextComposer.Compose(question, context, settings.RedactSecrets);
        Input = "";
        Items.Add(new AiUserItemViewModel(question, [.. context.Select(c => c.Label)]));
        OnPropertyChanged(nameof(HasMessages));
        if (composed.Redactions > 0) AddNotice(AiNoticeKind.Info, Loc.Instance.Format("ai.redacted", ("count", composed.Redactions)));
        // Terminal / selection chips are one-shot; the host chip stays for follow-up questions.
        foreach (var chip in Chips.Where(c => c.Item.Kind != AiContextKind.Host).ToList()) Chips.Remove(chip);
        OnPropertyChanged(nameof(HasChips));

        IsRunning = true;
        _run = new CancellationTokenSource();
        var ct = _run.Token;
        try
        {
            if (_conversation is null && settings.SaveHistory)
            {
                var (titleQuestion, _) = SplitContext(composed.Text);
                _conversation = new AiConversation
                {
                    Id = ConversationId, Title = Truncate(titleQuestion, 80), ProviderConfigId = providerConfig.Id, Model = model,
                };
                Title = _conversation.Title;
                await _owner.Store.SaveAsync(_conversation, CancellationToken.None);
            }
            var provider = await _owner.SettingsService.CreateProviderAsync(providerConfig, ct);
            var agent = new AiAgent(provider, _owner.Tools, new AgentOptions
            {
                MaxSteps = Math.Clamp(settings.MaxSteps, 1, 100),
                MaxToolOutputChars = settings.MaxToolOutputChars,
                RedactSecrets = settings.RedactSecrets,
            });
            var turn = new AgentTurn(ConversationId, _history.ToList(), composed.Text, model, providerConfig.Effort,
                AiTools.FromCatalog(_owner.Catalog), AiAgent.Actor(providerConfig, model),
                MaxTokens: providerConfig.MaxOutputTokens ?? 32000);
            await foreach (var ev in agent.RunAsync(turn, ct))
                await HandleAsync(ev);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            FinishStreaming();
            foreach (var card in _cards.Values.Where(c => c.State is AiToolCardState.Running or AiToolCardState.AwaitingApproval))
                card.Finish(new ToolResult(card.Call.Id, Loc.Instance["ai.stopped"], true));
            AddNotice(AiNoticeKind.Info, Loc.Instance["ai.stopped"]);
        }
        catch (Exception e) when (e is AiConversationUnreadableException or IOException or InvalidOperationException or HttpRequestException or VaultLockedException or SecretUnavailableException)
        {
            FinishStreaming();
            AddNotice(AiNoticeKind.Error, Loc.Instance.Format("ai.error.unexpected", ("error", e.Message)));
        }
        finally
        {
            IsRunning = false;
            _run.Dispose();
            _run = null;
        }
    }

    static string Truncate(string s, int max)
    {
        var line = s.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..max] + "…";
    }

    async Task HandleAsync(AgentEvent ev)
    {
        switch (ev)
        {
            case AgentStreamEvent { Event: TextDelta t }:
                Streaming().AppendText(t.Text);
                break;
            case AgentStreamEvent { Event: ThinkingDelta t }:
                Streaming().AppendThinking(t.Text);
                break;
            case AgentStreamEvent { Event: ToolCallStarted s }:
                FinishStreaming();
                if (!_cards.ContainsKey(s.Id))
                {
                    var card = NewCard(new ToolCall(s.Id, s.Name, null, ""));
                    _cards[s.Id] = card;
                    Items.Add(card);
                }
                break;
            case AgentStreamEvent { Event: ToolCallCompleted c }:
                if (_cards.TryGetValue(c.Call.Id, out var existing)) existing.Summary = SummarizeCall(c.Call);
                break;
            case AgentStreamEvent { Event: FallbackOccurred f }:
                AddNotice(AiNoticeKind.Info, Loc.Instance.Format("ai.fallback", ("from", f.FromModel), ("to", f.ToModel)));
                break;
            case AgentStreamEvent { Event: Usage u }:
                _inputTokens += u.InputTokens + u.CacheReadTokens + u.CacheWriteTokens;
                _outputTokens += u.OutputTokens;
                UpdateUsage();
                break;
            case AgentStreamEvent { Event: Stopped }:
                FinishStreaming();
                break;
            case AgentToolStarted s:
                if (!_cards.TryGetValue(s.Call.Id, out var started))
                {
                    started = NewCard(s.Call);
                    _cards[s.Call.Id] = started;
                    Items.Add(started);
                }
                started.Summary = SummarizeCall(s.Call);
                break;
            case AgentToolFinished f:
                if (_cards.TryGetValue(f.Call.Id, out var done)) done.Finish(f.Result);
                break;
            case AgentRedacted r:
                AddNotice(AiNoticeKind.Info, Loc.Instance.Format("ai.redacted", ("count", r.Count)));
                break;
            case AgentHistoryAppended a:
                _history.Add(a.Message);
                if (a.Message.Role == AiRole.Assistant)
                    foreach (var call in a.Message.ToolCalls.Where(c => !_cards.ContainsKey(c.Id)))
                        Items.Add(_cards[call.Id] = NewCard(call));
                await PersistAsync(a.Message);
                break;
            case AgentFinished f:
                FinishStreaming();
                switch (f.Stop)
                {
                    case AgentStop.Refusal:
                        AddNotice(AiNoticeKind.Warning, Loc.Instance.Format("ai.refusal", ("detail", f.Detail ?? "")));
                        break;
                    case AgentStop.MaxTokens:
                        AddNotice(AiNoticeKind.Warning, Loc.Instance["ai.maxTokens"]);
                        break;
                    case AgentStop.StepLimit:
                        AddNotice(AiNoticeKind.Warning, Loc.Instance.Format("ai.stepLimit", ("steps", f.Steps)));
                        break;
                    case AgentStop.Error:
                        AddNotice(AiNoticeKind.Error, Loc.Instance.Format("ai.error." + f.ErrorKind, ("detail", f.Detail ?? "")));
                        break;
                }
                break;
        }
    }

    AiAssistantItemViewModel Streaming()
    {
        if (_streaming is null)
        {
            _streaming = new AiAssistantItemViewModel { IsStreaming = true };
            Items.Add(_streaming);
            OnPropertyChanged(nameof(HasMessages));
        }
        return _streaming;
    }

    void FinishStreaming()
    {
        if (_streaming is null) return;
        _streaming.IsStreaming = false;
        _streaming = null;
    }

    async Task PersistAsync(AiMessage message)
    {
        if (_conversation is null) return; // history saving off
        var seq = _history.Count - 1;
        await _owner.Store.AppendMessageAsync(_conversation.Id,
            new AiStoredMessage(seq, (int)message.Role, AiHistorySerializer.Serialize(message), DateTimeOffset.UtcNow), CancellationToken.None);
        _conversation = _conversation with { UpdatedAt = DateTimeOffset.UtcNow, InputTokens = _inputTokens, OutputTokens = _outputTokens };
        await _owner.Store.SaveAsync(_conversation, CancellationToken.None);
    }

    /// <summary>The tool layer asks for approval of one of this chat's calls: shown on its card (design §7.6).</summary>
    internal async Task<ApprovalOutcome> RequestApprovalAsync(ApprovalRequest request, AiToolCallInfo info, CancellationToken ct)
    {
        if (!_cards.TryGetValue(info.ToolCallId, out var card))
        {
            card = NewCard(new ToolCall(info.ToolCallId, info.ToolName, null, request.Summary));
            _cards[info.ToolCallId] = card;
            Items.Add(card);
        }
        card.HostLabel = $"{request.Host.Label} ({request.Host.Hostname})";
        if (_sessionApprovals.Contains(SessionKey(request))) return ApprovalOutcome.Approved;
        var outcome = await card.AwaitApprovalAsync(request, ct);
        card.ApprovalEnded();
        return outcome;
    }
}

public static class AiConverters
{
    public static readonly Avalonia.Data.Converters.IValueConverter Chevron =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(open => open ? "chevron-up" : "chevron-down");

    public static readonly Avalonia.Data.Converters.IValueConverter NoticeKind =
        new Avalonia.Data.Converters.FuncValueConverter<AiNoticeKind, MySSH.UI.Carbon.NotificationKind>(k => k switch
        {
            AiNoticeKind.Error => MySSH.UI.Carbon.NotificationKind.Error,
            AiNoticeKind.Warning => MySSH.UI.Carbon.NotificationKind.Warning,
            _ => MySSH.UI.Carbon.NotificationKind.Info,
        });
}
