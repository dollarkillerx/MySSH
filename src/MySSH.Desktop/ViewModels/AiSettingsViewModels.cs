using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySSH.Ai;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.UI.Carbon;

namespace MySSH.Desktop.ViewModels;

/// <summary>A labelled choice for a Carbon drop-down (the label is looked up when shown, so it follows the language).</summary>
public sealed record AiEffortOption(AiEffort Value, string LabelKey)
{
    public string Label => Loc.Instance[LabelKey];
}

public sealed record AiThinkingOption(AiThinkingDisplay Value, string LabelKey)
{
    public string Label => Loc.Instance[LabelKey];
}

/// <summary>"Delete conversations after": null = keep.</summary>
public sealed record AiRetentionOption(int? Days)
{
    public string Label => Days is { } d ? Loc.Instance.Format("aiSettings.retention.days", ("days", d)) : Loc.Instance["aiSettings.retention.off"];
}

/// <summary>One row of the providers table.</summary>
public sealed class AiProviderRowViewModel(AiProviderConfig config, bool isDefault, bool lastTestFailed)
{
    public AiProviderConfig Config { get; } = config;
    public Guid Id => Config.Id;
    public string Name => Config.Name;
    public string ProtocolText => AiSettingsViewModel.ProtocolText(Config.Protocol);
    public string BaseUrl => Config.BaseUrl;
    public string ModelText => string.IsNullOrWhiteSpace(Config.Model) ? "—" : Config.Model;
    public bool IsDefault { get; } = isDefault;
    public bool LastTestFailed { get; } = lastTestFailed;
    public string StatusText => Loc.Instance[LastTestFailed ? "aiSettings.status.failed" : Config.Verified ? "aiSettings.status.verified" : "aiSettings.status.unverified"];
    public TagType StatusTag => LastTestFailed ? TagType.Red : Config.Verified ? TagType.Green : TagType.Gray;
}

/// <summary>"AI assistant" tab of the AI / MCP page: providers and behaviour (design §7.1, §7.8). Every change is saved
/// right away; provider edits are saved when the editor's Save is pressed.</summary>
public sealed partial class AiSettingsViewModel : ObservableObject, IDisposable
{
    public const int MaxTerminalLines = 2000;
    public const int MaxStepsLimit = 100;

    readonly SynchronizationContext? _sync = SynchronizationContext.Current;
    readonly SemaphoreSlim _saveLock = new(1, 1);
    readonly HashSet<Guid> _failed = [];
    bool _syncing;

    public AiSettingsViewModel(AiSettingsService service, IUiServices ui, IAiConversationStore? conversations = null)
    {
        Service = service;
        Ui = ui;
        Conversations = conversations;
        Service.Changed += () =>
        {
            if (_sync is null || _sync == SynchronizationContext.Current) Refresh();
            else _sync.Post(_ => Refresh(), null);
        };
        Refresh();
    }

    public AiSettingsService Service { get; }
    public IUiServices Ui { get; }
    public IAiConversationStore? Conversations { get; }

    public ObservableCollection<AiProviderRowViewModel> Providers { get; } = [];
    [ObservableProperty] public partial bool HasProviders { get; set; }
    [ObservableProperty] public partial AiProviderEditorViewModel? Editor { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }

    // ------------------------------------------------------------------ behaviour
    [ObservableProperty] public partial bool RedactSecrets { get; set; }
    [ObservableProperty] public partial bool AttachHostByDefault { get; set; }
    [ObservableProperty] public partial bool SaveHistory { get; set; }
    [ObservableProperty] public partial string TerminalLinesText { get; set; } = "";
    [ObservableProperty] public partial string? TerminalLinesError { get; set; }
    [ObservableProperty] public partial string MaxStepsText { get; set; } = "";
    [ObservableProperty] public partial string? MaxStepsError { get; set; }
    public ObservableCollection<AiRetentionOption> RetentionOptions { get; } = [new(null), new(7), new(30), new(90)];
    [ObservableProperty] public partial AiRetentionOption? SelectedRetention { get; set; }
    public bool CanDeleteConversations => Conversations is not null;

    /// <summary>The last behaviour save started by a property change (tests await it).</summary>
    public Task PendingSave { get; private set; } = Task.CompletedTask;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            await Service.LoadAsync(ct);
            Error = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = e.Message;
        }
        Refresh();
    }

    public void Refresh()
    {
        var s = Service.Current;
        var defaultId = s.DefaultProvider?.Id;
        Providers.Clear();
        foreach (var p in s.Providers) Providers.Add(new AiProviderRowViewModel(p, p.Id == defaultId, _failed.Contains(p.Id)));
        HasProviders = Providers.Count > 0;

        _syncing = true;
        try
        {
            RedactSecrets = s.RedactSecrets;
            AttachHostByDefault = s.AttachHostByDefault;
            SaveHistory = s.SaveHistory;
            if (TerminalLinesError is null || ParseInRange(TerminalLinesText, MaxTerminalLines) is not null)
            {
                TerminalLinesText = s.TerminalContextLines.ToString(Loc.Instance.Culture);
                TerminalLinesError = null;
            }
            if (MaxStepsError is null || ParseInRange(MaxStepsText, MaxStepsLimit) is not null)
            {
                MaxStepsText = s.MaxSteps.ToString(Loc.Instance.Culture);
                MaxStepsError = null;
            }
            var retention = RetentionOptions.FirstOrDefault(o => o.Days == s.RetentionDays);
            if (retention is null)
            {
                retention = new AiRetentionOption(s.RetentionDays);
                RetentionOptions.Add(retention);
            }
            SelectedRetention = retention;
        }
        finally { _syncing = false; }
    }

    internal static string ProtocolText(AiProtocol protocol) =>
        Loc.Instance[protocol == AiProtocol.Anthropic ? "aiSettings.protocol.anthropic" : "aiSettings.protocol.openai"];

    /// <summary>Serialized read-modify-write of the settings.</summary>
    internal async Task UpdateAsync(Func<AiSettings, AiSettings> change, CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            await Service.SaveAsync(change(Service.Current), ct);
            Error = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = e.Message;
        }
        finally { _saveLock.Release(); }
    }

    void SaveBehaviour(Func<AiSettings, AiSettings> change)
    {
        if (_syncing) return;
        var previous = PendingSave;
        PendingSave = Chain(previous, change);
    }

    async Task Chain(Task previous, Func<AiSettings, AiSettings> change)
    {
        await previous;
        await UpdateAsync(change);
    }

    partial void OnRedactSecretsChanged(bool value) => SaveBehaviour(s => s with { RedactSecrets = value });
    partial void OnAttachHostByDefaultChanged(bool value) => SaveBehaviour(s => s with { AttachHostByDefault = value });
    partial void OnSaveHistoryChanged(bool value) => SaveBehaviour(s => s with { SaveHistory = value });
    partial void OnSelectedRetentionChanged(AiRetentionOption? value)
    {
        if (value is not null) SaveBehaviour(s => s with { RetentionDays = value.Days });
    }

    partial void OnTerminalLinesTextChanged(string value)
    {
        if (_syncing) return;
        var n = ParseInRange(value, MaxTerminalLines);
        TerminalLinesError = n is null ? Loc.Instance.Format("aiSettings.validation.range", ("min", 1), ("max", MaxTerminalLines)) : null;
        if (n is { } lines) SaveBehaviour(s => s with { TerminalContextLines = lines });
    }

    partial void OnMaxStepsTextChanged(string value)
    {
        if (_syncing) return;
        var n = ParseInRange(value, MaxStepsLimit);
        MaxStepsError = n is null ? Loc.Instance.Format("aiSettings.validation.range", ("min", 1), ("max", MaxStepsLimit)) : null;
        if (n is { } steps) SaveBehaviour(s => s with { MaxSteps = steps });
    }

    static int? ParseInRange(string? text, int max) =>
        int.TryParse(text?.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= max ? n : null;

    // ------------------------------------------------------------------ providers

    /// <summary>Opens the editor for a new provider from a preset (the add-provider menu).</summary>
    [RelayCommand]
    public async Task AddProviderAsync(AiProviderKind kind)
    {
        await CloseEditorAsync();
        var preset = kind switch
        {
            AiProviderKind.Claude => AiProviderConfig.Claude(),
            AiProviderKind.OpenAI => AiProviderConfig.OpenAI(),
            AiProviderKind.DeepSeek => AiProviderConfig.DeepSeek(),
            _ => AiProviderConfig.Custom(AiProtocol.OpenAICompatible, ""),
        };
        Message = null;
        Editor = new AiProviderEditorViewModel(this, preset, isNew: true);
    }

    [RelayCommand]
    public async Task EditProviderAsync(AiProviderRowViewModel? row)
    {
        if (row is null) return;
        await CloseEditorAsync();
        Message = null;
        var editor = new AiProviderEditorViewModel(this, row.Config, isNew: false);
        Editor = editor;
        await editor.LoadAsync();
    }

    [RelayCommand]
    public async Task SetDefaultAsync(AiProviderRowViewModel? row)
    {
        if (row is null) return;
        await UpdateAsync(s => s with { DefaultProviderId = row.Id });
    }

    [RelayCommand]
    public async Task DeleteProviderAsync(AiProviderRowViewModel? row)
    {
        if (row is null) return;
        var loc = Loc.Instance;
        if (!await Ui.ConfirmAsync(loc["aiSettings.delete.title"], loc.Format("aiSettings.delete.body", ("name", row.Name)), loc["common.delete"], danger: true))
            return;
        if (Editor?.Original.Id == row.Id) await CloseEditorAsync();
        await _saveLock.WaitAsync();
        try
        {
            await Service.RemoveProviderAsync(row.Id);
            _failed.Remove(row.Id);
            Error = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = e.Message;
        }
        finally { _saveLock.Release(); }
        Refresh();
    }

    [RelayCommand]
    public async Task CloseEditorAsync()
    {
        if (Editor is not { } editor) return;
        Editor = null;
        await editor.DiscardAsync();
        editor.Dispose();
    }

    [RelayCommand]
    public async Task SaveEditorAsync()
    {
        if (Editor is not { } editor) return;
        if (await editor.SaveAsync())
        {
            Editor = null;
            editor.Dispose();
            Refresh();
        }
    }

    internal void MarkTestResult(Guid id, bool ok)
    {
        if (ok) _failed.Remove(id);
        else _failed.Add(id);
        Refresh();
    }

    internal void Saved(AiProviderConfig config) => _failed.Remove(config.Id);

    // ------------------------------------------------------------------ history

    [RelayCommand]
    public async Task DeleteAllConversationsAsync()
    {
        if (Conversations is null) return;
        var loc = Loc.Instance;
        if (!await Ui.ConfirmAsync(loc["aiSettings.history.deleteAll"], loc["aiSettings.history.deleteAllConfirm"], loc["common.delete"], danger: true))
            return;
        try
        {
            var n = await Conversations.DeleteAllAsync();
            Message = loc.Format("aiSettings.history.deleted", ("count", n));
            Error = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = e.Message;
        }
    }

    public void Dispose()
    {
        Editor?.Dispose();
        _saveLock.Dispose();
    }
}

/// <summary>Side-panel editor for one provider. The API key is never loaded into the field unless the user asks for it.</summary>
public sealed partial class AiProviderEditorViewModel : ObservableObject, IDisposable
{
    readonly AiSettingsViewModel _owner;
    readonly CancellationTokenSource _cts = new();
    string? _storedKey;
    /// <summary>A key this editor wrote to the vault during "Test connection" for a provider that was never saved.</summary>
    bool _wroteKeyForNew;
    bool _removeKey;
    /// <summary>The connection that the last successful test used (null = untested or failed).</summary>
    AiProviderConfig? _tested;
    string? _testedKey;
    bool _testFailed;
    /// <summary>The key in the vault when the editor opened ("Verified" only survives an unchanged connection and key).</summary>
    string? _openedKey;
    ProviderCapabilities? _detected;

    public AiProviderEditorViewModel(AiSettingsViewModel owner, AiProviderConfig original, bool isNew)
    {
        _owner = owner;
        Original = original;
        IsNew = isNew;
        Name = original.Name;
        BaseUrl = original.BaseUrl;
        ProtocolIsAnthropic = original.Protocol == AiProtocol.Anthropic;
        Model = original.Model ?? "";
        if (!string.IsNullOrWhiteSpace(original.Model)) Models.Add(original.Model);
        CapTools = original.Capabilities.Tools;
        CapStreaming = original.Capabilities.Streaming;
        SelectedEffort = EffortOptions.First(o => o.Value == original.Effort);
        SelectedThinking = ThinkingOptions.First(o => o.Value == original.ThinkingDisplay);
        ServerSideFallback = original.ServerSideFallback;
        EagerInputStreaming = original.EagerInputStreaming;
        UseBearerAuth = original.UseBearerAuth;
        HeadersText = string.Join(Environment.NewLine, original.ExtraHeaders.Select(h => $"{h.Key}: {h.Value}"));
    }

    public AiProviderConfig Original { get; }
    public bool IsNew { get; }
    public AiProviderKind Kind => Original.Kind;
    public bool IsCustom => Kind == AiProviderKind.Custom;
    public string Heading => IsNew
        ? Loc.Instance.Format("aiSettings.editor.addTitle", ("name", KindLabel(Kind)))
        : Loc.Instance.Format("aiSettings.editor.editTitle", ("name", Original.Name));

    public static string KindLabel(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Claude => "Claude",
        AiProviderKind.OpenAI => "OpenAI",
        AiProviderKind.DeepSeek => "DeepSeek",
        _ => Loc.Instance["aiSettings.preset.custom"],
    };

    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial string? NameError { get; set; }
    [ObservableProperty] public partial string BaseUrl { get; set; }
    [ObservableProperty] public partial string? BaseUrlError { get; set; }

    /// <summary>Custom only: Anthropic-compatible vs OpenAI-compatible.</summary>
    [ObservableProperty] public partial bool ProtocolIsAnthropic { get; set; }
    public bool ProtocolIsOpenAI
    {
        get => !ProtocolIsAnthropic;
        set { if (value) ProtocolIsAnthropic = false; }
    }
    /// <summary>Claude-only options (effort, thinking, fallback, eager streaming, bearer auth).</summary>
    public bool IsAnthropic => ProtocolIsAnthropic;
    public string BaseUrlPlaceholder => ProtocolIsAnthropic ? "https://gateway.example.com" : "http://127.0.0.1:11434/v1";

    partial void OnProtocolIsAnthropicChanged(bool value)
    {
        OnPropertyChanged(nameof(ProtocolIsOpenAI));
        OnPropertyChanged(nameof(IsAnthropic));
        OnPropertyChanged(nameof(BaseUrlPlaceholder));
    }

    // key
    [ObservableProperty] public partial string ApiKey { get; set; } = "";
    [ObservableProperty] public partial string? ApiKeyError { get; set; }
    /// <summary>A key is in the vault and the field is still empty (it is loaded only by <see cref="ShowStoredKeyCommand"/>).</summary>
    [ObservableProperty] public partial bool HasHiddenStoredKey { get; set; }
    /// <summary>A key is in the vault (shown or not) and has not been marked for removal.</summary>
    [ObservableProperty] public partial bool HasStoredKey { get; set; }
    [ObservableProperty] public partial bool IsKeyRemoved { get; set; }
    public bool KeyRequired => !IsCustom;
    public string ApiKeyHelp => Loc.Instance[IsKeyRemoved ? "aiSettings.key.removedHelp"
        : HasHiddenStoredKey ? "aiSettings.key.savedHelp"
        : IsCustom ? "aiSettings.key.optionalHelp" : "aiSettings.key.help"];

    partial void OnHasHiddenStoredKeyChanged(bool value) => OnPropertyChanged(nameof(ApiKeyHelp));
    partial void OnIsKeyRemovedChanged(bool value) => OnPropertyChanged(nameof(ApiKeyHelp));
    partial void OnApiKeyChanged(string value)
    {
        ApiKeyError = null;
        if (value.Length > 0 && IsKeyRemoved) IsKeyRemoved = false;
    }

    // model
    [ObservableProperty] public partial string Model { get; set; }
    [ObservableProperty] public partial string? ModelError { get; set; }
    public ObservableCollection<string> Models { get; } = [];
    [ObservableProperty] public partial bool IsFetchingModels { get; set; }
    [ObservableProperty] public partial string? ModelsMessage { get; set; }

    partial void OnModelChanged(string value) => ModelError = null;

    // capabilities (Custom)
    [ObservableProperty] public partial bool CapTools { get; set; }
    [ObservableProperty] public partial bool CapStreaming { get; set; }

    // Claude options
    public IReadOnlyList<AiEffortOption> EffortOptions { get; } =
        [new(AiEffort.Low, "aiSettings.effort.low"), new(AiEffort.Medium, "aiSettings.effort.medium"), new(AiEffort.High, "aiSettings.effort.high")];
    [ObservableProperty] public partial AiEffortOption SelectedEffort { get; set; }
    public IReadOnlyList<AiThinkingOption> ThinkingOptions { get; } =
    [
        new(AiThinkingDisplay.Summarized, "aiSettings.thinking.summarized"),
        new(AiThinkingDisplay.Updates, "aiSettings.thinking.updates"),
        new(AiThinkingDisplay.Omitted, "aiSettings.thinking.omitted"),
    ];
    [ObservableProperty] public partial AiThinkingOption SelectedThinking { get; set; }
    [ObservableProperty] public partial bool ServerSideFallback { get; set; }
    [ObservableProperty] public partial bool EagerInputStreaming { get; set; }
    [ObservableProperty] public partial bool UseBearerAuth { get; set; }

    // headers
    [ObservableProperty] public partial string HeadersText { get; set; }
    [ObservableProperty] public partial string? HeadersError { get; set; }

    // test
    [ObservableProperty] public partial bool IsTesting { get; set; }
    [ObservableProperty] public partial string? TestSuccess { get; set; }
    [ObservableProperty] public partial string? TestError { get; set; }
    [ObservableProperty] public partial bool IsSaving { get; set; }
    [ObservableProperty] public partial string? SaveError { get; set; }
    public bool IsBusy => IsTesting || IsFetchingModels || IsSaving;
    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnIsFetchingModelsChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    public CancellationToken Lifetime => _cts.Token;

    /// <summary>Existing providers: find out whether a key is stored (it stays out of the field).</summary>
    public async Task LoadAsync()
    {
        if (IsNew) return;
        try
        {
            _storedKey = await _owner.Service.GetApiKeyAsync(Original, _cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            _storedKey = null;
            ApiKeyError = Loc.Instance.Format("aiSettings.key.unreadable", ("error", e.Message));
        }
        if (string.IsNullOrEmpty(_storedKey)) _storedKey = null;
        _openedKey = _storedKey;
        HasStoredKey = HasHiddenStoredKey = _storedKey is not null;
    }

    [RelayCommand]
    void ShowStoredKey()
    {
        if (_storedKey is null) return;
        ApiKey = _storedKey;
        HasHiddenStoredKey = false;
    }

    [RelayCommand]
    void RemoveKey()
    {
        ApiKey = "";
        HasHiddenStoredKey = false;
        HasStoredKey = false;
        IsKeyRemoved = true;
        _removeKey = true;
    }

    /// <summary>The key the provider would use right now (typed, else the stored one unless removed).</summary>
    string? EffectiveKey() => ApiKey.Trim().Length > 0 ? ApiKey.Trim() : _removeKey ? null : _storedKey;

    // ------------------------------------------------------------------ validation / building

    bool ValidateConnection(out IReadOnlyDictionary<string, string> headers)
    {
        var url = BaseUrl.Trim();
        BaseUrlError = url.Length == 0 ? Loc.Instance["validation.required"]
            : Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.Host.Length > 0
                ? null : Loc.Instance["aiSettings.validation.url"];
        HeadersError = ParseHeaders(HeadersText, out headers);
        return BaseUrlError is null && HeadersError is null;
    }

    bool ValidateAll(out IReadOnlyDictionary<string, string> headers)
    {
        var ok = ValidateConnection(out headers);
        NameError = string.IsNullOrWhiteSpace(Name) ? Loc.Instance["validation.required"] : null;
        ModelError = string.IsNullOrWhiteSpace(Model) ? Loc.Instance["aiSettings.validation.model"] : null;
        ApiKeyError = KeyRequired && EffectiveKey() is null ? Loc.Instance["aiSettings.validation.key"] : null;
        return ok && NameError is null && ModelError is null && ApiKeyError is null;
    }

    /// <summary>"Name: value" lines. Authorization / x-api-key are refused (the key has its own field).</summary>
    public static string? ParseHeaders(string? text, out IReadOnlyDictionary<string, string> headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        headers = result;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var name = colon > 0 ? line[..colon].Trim() : "";
            if (name.Length == 0 || !name.All(IsTokenChar))
                return Loc.Instance.Format("aiSettings.validation.headerLine", ("line", lineNo));
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase))
                return Loc.Instance.Format("aiSettings.validation.headerKey", ("name", name));
            if (result.ContainsKey(name))
                return Loc.Instance.Format("aiSettings.validation.headerDuplicate", ("name", name));
            result[name] = line[(colon + 1)..].Trim();
        }
        return null;
    }

    static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal);

    AiProviderConfig Build(IReadOnlyDictionary<string, string> headers) => Original with
    {
        Name = Name.Trim(),
        BaseUrl = BaseUrl.Trim(),
        Protocol = IsCustom ? (ProtocolIsAnthropic ? AiProtocol.Anthropic : AiProtocol.OpenAICompatible) : Original.Protocol,
        Model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim(),
        ExtraHeaders = headers,
        Capabilities = IsCustom ? Original.Capabilities with { Tools = CapTools, Streaming = CapStreaming } : Original.Capabilities,
        Effort = SelectedEffort.Value,
        ThinkingDisplay = SelectedThinking.Value,
        ServerSideFallback = ServerSideFallback,
        EagerInputStreaming = EagerInputStreaming,
        UseBearerAuth = UseBearerAuth,
    };

    static bool SameConnection(AiProviderConfig a, AiProviderConfig b) =>
        a.BaseUrl == b.BaseUrl && a.Protocol == b.Protocol && a.Model == b.Model && a.UseBearerAuth == b.UseBearerAuth
        && a.ExtraHeaders.Count == b.ExtraHeaders.Count
        && a.ExtraHeaders.All(h => b.ExtraHeaders.TryGetValue(h.Key, out var v) && v == h.Value);

    /// <summary>Writes a typed or removed key to the vault. Returns false (with <see cref="ApiKeyError"/>) on failure.</summary>
    async Task<bool> ApplyKeyAsync(AiProviderConfig config, CancellationToken ct)
    {
        var typed = ApiKey.Trim();
        try
        {
            if (typed.Length > 0 && typed != _storedKey)
            {
                await _owner.Service.SetApiKeyAsync(config, typed, ct);
                _storedKey = typed;
                if (IsNew) _wroteKeyForNew = true;
                HasStoredKey = true;
                HasHiddenStoredKey = false;
            }
            else if (typed.Length == 0 && _removeKey && _storedKey is not null)
            {
                await _owner.Service.SetApiKeyAsync(config, null, ct);
                _storedKey = null;
            }
            _removeKey = false;
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ApiKeyError = e.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ actions

    [RelayCommand]
    public async Task FetchModelsAsync()
    {
        ModelsMessage = null;
        if (!ValidateConnection(out var headers)) return;
        IsFetchingModels = true;
        try
        {
            var config = Build(headers);
            var provider = _owner.Service.ProviderFactory(config, EffectiveKey(), _owner.Service.Http);
            var models = await provider.ListModelsAsync(_cts.Token);
            SetModels(models);
            ModelsMessage = models.Count == 0 ? Loc.Instance["aiSettings.models.none"] : Loc.Instance.Format("aiSettings.models.found", ("count", models.Count));
            ModelError = null;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception e)
        {
            ModelError = Loc.Instance.Format("aiSettings.models.failed", ("error", e.Message));
        }
        finally { IsFetchingModels = false; }
    }

    void SetModels(IReadOnlyList<ModelInfo> models)
    {
        var keep = Model;
        Models.Clear();
        foreach (var id in models.Select(m => m.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) Models.Add(id);
        if (keep.Length > 0 && !Models.Contains(keep)) Models.Insert(0, keep);
        Model = keep;
    }

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        TestSuccess = TestError = null;
        if (!ValidateConnection(out var headers)) return;
        ModelError = string.IsNullOrWhiteSpace(Model) ? Loc.Instance["aiSettings.validation.model"] : null;
        if (ModelError is not null) return;
        IsTesting = true;
        var config = Build(headers);
        try
        {
            if (!await ApplyKeyAsync(config, _cts.Token)) return;
            var provider = await _owner.Service.CreateProviderAsync(config, _cts.Token);
            var result = await provider.ProbeAsync(config.Model!, _cts.Token);
            if (_cts.IsCancellationRequested) return;
            if (result.Ok)
            {
                if (result.Models.Count > 0) SetModels(result.Models);
                if (IsCustom)
                {
                    CapTools = result.Detected.Tools;
                    CapStreaming = result.Detected.Streaming;
                }
                _tested = config;
                _testedKey = _storedKey;
                _testFailed = false;
                var loc = Loc.Instance;
                TestSuccess = loc.Format("aiSettings.test.ok",
                    ("count", result.Models.Count),
                    ("tools", loc[result.Detected.Tools ? "aiSettings.yes" : "aiSettings.no"]),
                    ("streaming", loc[result.Detected.Streaming ? "aiSettings.yes" : "aiSettings.no"]));
                _detected = result.Detected;
            }
            else
            {
                _tested = null;
                _testFailed = true;
                TestError = string.IsNullOrWhiteSpace(result.Error) ? Loc.Instance["aiSettings.test.failedUnknown"] : result.Error;
            }
            if (!IsNew) _owner.MarkTestResult(Original.Id, result.Ok);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception e)
        {
            _tested = null;
            _testFailed = true;
            TestError = e.Message;
            if (!IsNew) _owner.MarkTestResult(Original.Id, false);
        }
        finally { IsTesting = false; }
    }

    /// <summary>Validates and saves the provider (and its key). False = stays open with field errors.</summary>
    public async Task<bool> SaveAsync()
    {
        SaveError = null;
        if (!ValidateAll(out var headers)) return false;
        IsSaving = true;
        try
        {
            var config = Build(headers);
            if (IsCustom && _detected is { } d && _tested is not null && SameConnection(_tested, config))
                config = config with { Capabilities = config.Capabilities with { Images = d.Images } };
            if (!await ApplyKeyAsync(config, _cts.Token)) return false;
            var verified = _tested is not null
                ? SameConnection(_tested, config) && _testedKey == _storedKey
                : !_testFailed && !IsNew && Original.Verified && SameConnection(Original, config) && _storedKey == _openedKey;
            config = config with { Verified = verified };
            await _owner.UpdateAsync(s =>
            {
                var list = s.Providers.ToList();
                var i = list.FindIndex(p => p.Id == config.Id);
                if (i >= 0) list[i] = config;
                else list.Add(config);
                return s with { Providers = list, DefaultProviderId = s.DefaultProviderId is { } id && list.Any(p => p.Id == id) ? id : config.Id };
            }, _cts.Token);
            if (_owner.Error is { } err)
            {
                SaveError = err;
                return false;
            }
            _wroteKeyForNew = false;
            _owner.Saved(config);
            ClearSecrets();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally { IsSaving = false; }
    }

    /// <summary>Closed without saving: cancel running requests, drop a key written for a never-saved provider.</summary>
    public async Task DiscardAsync()
    {
        await _cts.CancelAsync();
        if (_wroteKeyForNew)
        {
            try { await _owner.Service.SetApiKeyAsync(Original, null); }
            catch (Exception e) when (e is not OperationCanceledException) { _owner.Error = e.Message; }
            _wroteKeyForNew = false;
        }
        ClearSecrets();
    }

    public void Dispose() => _cts.Dispose();

    void ClearSecrets()
    {
        ApiKey = "";
        _storedKey = null;
        _testedKey = null;
    }
}
