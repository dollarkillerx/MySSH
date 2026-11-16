using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MySSH.Ai;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.Desktop.Views;
using MySSH.Storage;

namespace MySSH.Desktop.Tests;

/// <summary>P4 "AI assistant" settings tab: providers (presets, validation, keys, models, test connection, default,
/// delete) and behaviour, against a real temp storage and a scripted provider.</summary>
[Collection("Loc")]
public sealed class AiSettingsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Scripted provider: records the key/config it was created with.</summary>
    sealed class FakeProvider : IChatProvider
    {
        public List<ModelInfo> Models { get; } = [new("model-b"), new("model-a")];
        public ProviderProbeResult? Probe { get; set; }
        public Exception? Throw { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public string? Key { get; set; }
        public AiProviderConfig? Config { get; set; }
        public int ProbeCalls { get; private set; }
        public string ProviderId => "fake";
        public ProviderCapabilities Capabilities => new();

        public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
        {
            if (Throw is { } e) throw e;
            await Task.Yield();
            return Models;
        }

        public async Task<ProviderProbeResult> ProbeAsync(string model, CancellationToken ct)
        {
            ProbeCalls++;
            if (Gate is { } g) await g.Task.WaitAsync(ct);
            if (Throw is { } e) throw e;
            return Probe ?? new ProviderProbeResult(true, Models, new ProviderCapabilities(Tools: true, Streaming: true));
        }

        public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    sealed class Fixture : IAsyncDisposable
    {
        public required TestStore Store { get; init; }
        public required AiSettingsService Service { get; init; }
        public required AiSettingsViewModel Vm { get; init; }
        public required FakeUi U { get; init; }
        public required FakeProvider P { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            Loc.Instance.SetCulture("en-US");
            var store = await TestStore.OpenAsync();
            var service = new AiSettingsService(store.Storage.Settings, store.Storage.Secrets);
            var provider = new FakeProvider();
            service.ProviderFactory = (config, key, http) =>
            {
                provider.Key = key;
                provider.Config = config;
                return provider;
            };
            await service.LoadAsync();
            var ui = new FakeUi();
            return new Fixture { Store = store, Service = service, U = ui, P = provider, Vm = new AiSettingsViewModel(service, ui, store.Storage.AiConversations) };
        }

        /// <summary>What a fresh load from storage returns.</summary>
        public async Task<AiSettings> ReloadAsync() =>
            await new AiSettingsService(Store.Storage.Settings, Store.Storage.Secrets).LoadAsync();

        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            Service.Dispose();
            await Store.DisposeAsync();
        }
    }

    static async Task<AiProviderConfig> AddClaudeAsync(Fixture f, string key = "sk-ant-test-1")
    {
        await f.Vm.AddProviderAsync(AiProviderKind.Claude);
        f.Vm.Editor!.ApiKey = key;
        await f.Vm.SaveEditorAsync();
        Assert.Null(f.Vm.Editor);
        return f.Service.Current.Providers[^1];
    }

    static async Task<AiProviderConfig> AddOllamaAsync(Fixture f, string? key = null)
    {
        await f.Vm.AddProviderAsync(AiProviderKind.Custom);
        var e = f.Vm.Editor!;
        e.Name = "Ollama";
        e.BaseUrl = "http://127.0.0.1:11434/v1";
        e.Model = "qwen3";
        if (key is not null) e.ApiKey = key;
        await f.Vm.SaveEditorAsync();
        Assert.Null(f.Vm.Editor);
        return f.Service.Current.Providers[^1];
    }

    [Fact]
    public async Task Each_preset_opens_with_its_defaults_and_saves()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.False(f.Vm.HasProviders);

        await f.Vm.AddProviderAsync(AiProviderKind.Claude);
        var e = f.Vm.Editor!;
        Assert.True(e.IsNew);
        Assert.False(e.IsCustom);
        Assert.True(e.IsAnthropic);
        Assert.Equal("https://api.anthropic.com", e.BaseUrl);
        Assert.Equal("claude-opus-5-5", e.Model);
        Assert.True(e.EagerInputStreaming);
        Assert.True(e.ServerSideFallback);
        Assert.Equal(AiEffort.Medium, e.SelectedEffort.Value);
        Assert.Equal("Add Claude", e.Heading);
        e.ApiKey = "sk-ant-1";
        e.SelectedEffort = e.EffortOptions.Single(o => o.Value == AiEffort.High);
        e.SelectedThinking = e.ThinkingOptions.Single(o => o.Value == AiThinkingDisplay.Omitted);
        e.UseBearerAuth = true;
        await f.Vm.SaveEditorAsync();
        Assert.Null(f.Vm.Editor);

        foreach (var (kind, url) in new[] { (AiProviderKind.OpenAI, "https://api.openai.com/v1"), (AiProviderKind.DeepSeek, "https://api.deepseek.com") })
        {
            await f.Vm.AddProviderAsync(kind);
            e = f.Vm.Editor!;
            Assert.Equal(url, e.BaseUrl);
            Assert.Equal("", e.Model); // no hard-coded default: the user picks
            Assert.False(e.IsAnthropic);
            e.ApiKey = "sk-" + kind;
            await f.Vm.SaveEditorAsync();
            Assert.NotNull(f.Vm.Editor); // model required
            Assert.NotNull(f.Vm.Editor!.ModelError);
            e.Model = kind == AiProviderKind.OpenAI ? "gpt-x" : "deepseek-chat";
            await f.Vm.SaveEditorAsync();
            Assert.Null(f.Vm.Editor);
        }

        await f.Vm.AddProviderAsync(AiProviderKind.Custom);
        e = f.Vm.Editor!;
        Assert.True(e.IsCustom);
        Assert.False(e.EagerInputStreaming); // custom gateways may reject it
        Assert.True(e.ProtocolIsOpenAI);
        e.ProtocolIsAnthropic = true;
        Assert.True(e.IsAnthropic);
        e.Name = "Gateway";
        e.BaseUrl = "https://gw.example.com";
        e.Model = "claude-sonnet";
        e.CapStreaming = false;
        e.HeadersText = "X-Project: myssh\nX-Team :  ops ";
        await f.Vm.SaveEditorAsync(); // keyless custom is allowed
        Assert.Null(f.Vm.Editor);

        var saved = await f.ReloadAsync();
        Assert.Equal(4, saved.Providers.Count);
        var claude = saved.Providers[0];
        Assert.Equal((AiProviderKind.Claude, AiEffort.High, AiThinkingDisplay.Omitted, true), (claude.Kind, claude.Effort, claude.ThinkingDisplay, claude.UseBearerAuth));
        Assert.Equal(claude.Id, saved.DefaultProviderId); // the first provider becomes the default
        Assert.Equal("gpt-x", saved.Providers[1].Model);
        Assert.Equal(AiProtocol.OpenAICompatible, saved.Providers[2].Protocol);
        var gw = saved.Providers[3];
        Assert.Equal((AiProviderKind.Custom, AiProtocol.Anthropic, false, false), (gw.Kind, gw.Protocol, gw.Capabilities.Streaming, gw.EagerInputStreaming));
        Assert.Equal("myssh", gw.ExtraHeaders["X-Project"]);
        Assert.Equal("ops", gw.ExtraHeaders["X-Team"]);
        Assert.All(saved.Providers, p => Assert.False(p.Verified));

        Assert.Equal(4, f.Vm.Providers.Count);
        Assert.Equal(["Claude", "OpenAI", "DeepSeek", "Gateway"], f.Vm.Providers.Select(r => r.Name));
        Assert.True(f.Vm.Providers[0].IsDefault);
        Assert.Equal("Anthropic-compatible", f.Vm.Providers[0].ProtocolText);
        Assert.Equal("Not verified", f.Vm.Providers[0].StatusText);
    }

    [Fact]
    public async Task Validation_keeps_the_editor_open_with_field_errors()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Vm.AddProviderAsync(AiProviderKind.Custom);
        var e = f.Vm.Editor!;
        e.Name = "  ";
        e.BaseUrl = "ftp://models.local";
        e.HeadersText = "Authorization: Bearer abc";
        await f.Vm.SaveEditorAsync();
        Assert.Same(e, f.Vm.Editor);
        Assert.NotNull(e.NameError);
        Assert.Equal("Enter an absolute http:// or https:// URL.", e.BaseUrlError);
        Assert.Contains("Authorization", e.HeadersError, StringComparison.Ordinal);
        Assert.NotNull(e.ModelError);
        Assert.Null(e.ApiKeyError); // keyless custom is fine

        foreach (var (headers, ok) in new[] { ("x-api-key: k", false), ("no colon here", false), ("Bad Name: v", false), ("A: 1\na: 2", false), ("X-A: 1\n\nX-B: two words", true) })
        {
            Assert.Equal(ok, AiProviderEditorViewModel.ParseHeaders(headers, out _) is null);
        }
        foreach (var (url, ok) in new[] { ("relative/path", false), ("http://", false), ("http://127.0.0.1:11434/v1", true), ("https://gw.example.com", true) })
        {
            e.BaseUrl = url;
            e.Name = "n";
            e.Model = "m";
            e.HeadersText = "";
            await e.FetchModelsAsync(); // validates the connection fields only
            Assert.Equal(ok, e.BaseUrlError is null);
        }
        Assert.Empty((await f.ReloadAsync()).Providers);

        await f.Vm.AddProviderAsync(AiProviderKind.Claude); // opening another preset discards the first
        await f.Vm.SaveEditorAsync();
        Assert.Equal("Enter the API key.", f.Vm.Editor!.ApiKeyError);
        Assert.Empty(f.Service.Current.Providers);
    }

    [Fact]
    public async Task Api_key_is_stored_encrypted_kept_when_empty_and_removed_on_request()
    {
        await using var f = await Fixture.CreateAsync();
        const string Key = "sk-ant-unique-7f3a9c";
        var claude = await AddClaudeAsync(f, Key);
        Assert.Equal(Key, await f.Service.GetApiKeyAsync(claude, Ct));
        Assert.Equal(Key, await f.Store.Storage.Secrets.GetAsync(claude.ApiKeySecretName, Ct));
        // not in the settings JSON, and not in plain text anywhere in the database files
        Assert.DoesNotContain(Key, await f.Store.Storage.Settings.GetJsonAsync(AiSettings.Key, Ct) ?? "", StringComparison.Ordinal);
        Assert.False(DatabaseContains(f.Store.Storage.Paths.DatabasePath, Key));

        // edit: the key stays out of the field until "Show saved key"
        await f.Vm.EditProviderAsync(f.Vm.Providers[0]);
        var e = f.Vm.Editor!;
        Assert.False(e.IsNew);
        Assert.Equal("", e.ApiKey);
        Assert.True(e.HasHiddenStoredKey);
        Assert.True(e.HasStoredKey);
        Assert.Equal("A key is saved. Leave the field empty to keep it.", e.ApiKeyHelp);
        e.Name = "Claude (work)";
        await f.Vm.SaveEditorAsync(); // empty field keeps the stored key
        Assert.Null(f.Vm.Editor);
        Assert.Equal(Key, await f.Service.GetApiKeyAsync(claude, Ct));
        Assert.Equal("Claude (work)", (await f.ReloadAsync()).Providers[0].Name);

        await f.Vm.EditProviderAsync(f.Vm.Providers[0]);
        e = f.Vm.Editor!;
        e.ShowStoredKeyCommand.Execute(null);
        Assert.Equal(Key, e.ApiKey);
        Assert.False(e.HasHiddenStoredKey);
        e.ApiKey = "sk-ant-new";
        await f.Vm.SaveEditorAsync();
        Assert.Equal("sk-ant-new", await f.Service.GetApiKeyAsync(claude, Ct));

        // remove: explicit action, applied on save (keyless only for custom providers)
        var ollama = await AddOllamaAsync(f, key: "local-key");
        Assert.Equal("local-key", await f.Service.GetApiKeyAsync(ollama, Ct));
        await f.Vm.EditProviderAsync(f.Vm.Providers[1]);
        e = f.Vm.Editor!;
        e.RemoveKeyCommand.Execute(null);
        Assert.True(e.IsKeyRemoved);
        Assert.False(e.HasStoredKey);
        Assert.Equal("local-key", await f.Service.GetApiKeyAsync(ollama, Ct)); // not yet
        await f.Vm.SaveEditorAsync();
        Assert.Null(await f.Service.GetApiKeyAsync(ollama, Ct));

        // Claude without a key cannot be saved
        await f.Vm.EditProviderAsync(f.Vm.Providers[0]);
        f.Vm.Editor!.RemoveKeyCommand.Execute(null);
        await f.Vm.SaveEditorAsync();
        Assert.NotNull(f.Vm.Editor!.ApiKeyError);
        await f.Vm.CloseEditorAsync();
        Assert.Equal("sk-ant-new", await f.Service.GetApiKeyAsync(claude, Ct)); // cancel keeps it
    }

    static bool DatabaseContains(string dbPath, string text)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var needle = Encoding.UTF8.GetBytes(text);
        foreach (var path in new[] { dbPath, dbPath + "-wal" })
        {
            if (!File.Exists(path)) continue;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            if (ms.ToArray().AsSpan().IndexOf(needle) >= 0) return true;
        }
        return false;
    }

    [Fact]
    public async Task Fetch_models_fills_the_list_with_the_typed_key()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Vm.AddProviderAsync(AiProviderKind.OpenAI);
        var e = f.Vm.Editor!;
        e.ApiKey = "sk-typed";
        await e.FetchModelsAsync();
        Assert.Equal(["model-a", "model-b"], e.Models);
        Assert.Equal("2 models found.", e.ModelsMessage);
        Assert.Equal("sk-typed", f.P.Key); // used without being saved
        Assert.Null(await f.Service.GetApiKeyAsync(e.Original, Ct));
        Assert.Equal("https://api.openai.com/v1", f.P.Config!.BaseUrl);
        e.Model = "model-b";

        e.Model = "custom-model"; // free text stays and is kept in the list
        await e.FetchModelsAsync();
        Assert.Equal("custom-model", e.Model);
        Assert.Contains("custom-model", e.Models);

        f.P.Throw = new AiProviderException(AiErrorKind.Authentication, "401: invalid x-api-key", 401);
        await e.FetchModelsAsync();
        Assert.Equal("Could not fetch models: 401: invalid x-api-key", e.ModelError);
        Assert.False(e.IsFetchingModels);
    }

    [Fact]
    public async Task Test_connection_success_verifies_and_stores_detected_capabilities()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Vm.AddProviderAsync(AiProviderKind.Custom);
        var e = f.Vm.Editor!;
        e.Name = "Ollama";
        e.BaseUrl = "http://127.0.0.1:11434/v1";
        await e.TestConnectionAsync();
        Assert.NotNull(e.ModelError); // needs a model
        Assert.Equal(0, f.P.ProbeCalls);

        e.Model = "qwen3";
        e.ApiKey = "local";
        f.P.Probe = new ProviderProbeResult(true, [new("qwen3"), new("llama4")], new ProviderCapabilities(Tools: false, Streaming: true, Images: true));
        await e.TestConnectionAsync();
        Assert.Null(e.TestError);
        Assert.Equal("2 models available. Tools: no. Streaming: yes.", e.TestSuccess);
        Assert.False(e.CapTools);
        Assert.True(e.CapStreaming);
        Assert.Equal(["llama4", "qwen3"], e.Models);
        Assert.Equal("local", await f.Service.GetApiKeyAsync(e.Original, Ct)); // the key was saved for the test
        Assert.Equal("local", f.P.Key);
        Assert.False(e.IsTesting);

        await f.Vm.SaveEditorAsync();
        var saved = (await f.ReloadAsync()).Providers.Single();
        Assert.True(saved.Verified);
        Assert.Equal(new ProviderCapabilities(Tools: false, Streaming: true, Images: true), saved.Capabilities);
        Assert.Equal("Verified", f.Vm.Providers[0].StatusText);

        // an unchanged edit keeps "verified"; a changed connection drops it
        await f.Vm.EditProviderAsync(f.Vm.Providers[0]);
        f.Vm.Editor!.Name = "Ollama (laptop)";
        await f.Vm.SaveEditorAsync();
        Assert.True(f.Service.Current.Providers[0].Verified);
        await f.Vm.EditProviderAsync(f.Vm.Providers[0]);
        f.Vm.Editor!.BaseUrl = "http://10.0.0.9:11434/v1";
        await f.Vm.SaveEditorAsync();
        Assert.False(f.Service.Current.Providers[0].Verified);
    }

    [Fact]
    public async Task Test_connection_failure_is_shown_and_marks_the_row()
    {
        await using var f = await Fixture.CreateAsync();
        await AddClaudeAsync(f);
        await f.Vm.EditProviderAsync(f.Vm.Providers[0]);
        var e = f.Vm.Editor!;
        f.P.Probe = new ProviderProbeResult(false, [], new ProviderCapabilities(), "HTTP 401: invalid x-api-key");
        await e.TestConnectionAsync();
        Assert.Null(e.TestSuccess);
        Assert.Equal("HTTP 401: invalid x-api-key", e.TestError);
        Assert.Equal("Last test failed", f.Vm.Providers[0].StatusText);
        Assert.Equal(UI.Carbon.TagType.Red, f.Vm.Providers[0].StatusTag);

        f.P.Probe = null;
        f.P.Throw = new NotImplementedException("probe not implemented yet");
        await e.TestConnectionAsync();
        Assert.Equal("probe not implemented yet", e.TestError);
        Assert.False(e.IsTesting);

        f.P.Throw = null;
        await e.TestConnectionAsync();
        Assert.Null(e.TestError);
        Assert.Equal("Not verified", f.Vm.Providers[0].StatusText); // a passed test clears "failed"; saving verifies
        await f.Vm.SaveEditorAsync();
        Assert.True(f.Service.Current.Providers[0].Verified);
        Assert.Equal("Verified", f.Vm.Providers[0].StatusText);
    }

    [Fact]
    public async Task Closing_the_editor_cancels_a_running_test_and_drops_the_key_of_an_unsaved_provider()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Vm.AddProviderAsync(AiProviderKind.Claude);
        var e = f.Vm.Editor!;
        e.ApiKey = "sk-ant-pending";
        f.P.Gate = new TaskCompletionSource();
        var test = e.TestConnectionAsync();
        while (f.P.ProbeCalls == 0) await Task.Delay(10, Ct);
        Assert.True(e.IsTesting);
        Assert.Equal("sk-ant-pending", await f.Service.GetApiKeyAsync(e.Original, Ct));
        await f.Vm.CloseEditorAsync();
        await test.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(e.IsTesting);
        Assert.Null(e.TestError);
        Assert.Null(await f.Service.GetApiKeyAsync(e.Original, Ct));
        Assert.Empty(f.Service.Current.Providers);
    }

    [Fact]
    public async Task Default_provider_can_be_changed_and_moves_when_deleted()
    {
        await using var f = await Fixture.CreateAsync();
        var claude = await AddClaudeAsync(f);
        var ollama = await AddOllamaAsync(f, "k");
        Assert.Equal(claude.Id, f.Service.Current.DefaultProviderId);
        Assert.Equal([true, false], f.Vm.Providers.Select(r => r.IsDefault));

        await f.Vm.SetDefaultAsync(f.Vm.Providers[1]);
        Assert.Equal(ollama.Id, (await f.ReloadAsync()).DefaultProviderId);
        Assert.Equal([false, true], f.Vm.Providers.Select(r => r.IsDefault));

        // delete: cancelled confirm keeps it
        f.U.ConfirmResult = false;
        await f.Vm.DeleteProviderAsync(f.Vm.Providers[1]);
        Assert.Equal(2, f.Service.Current.Providers.Count);
        Assert.Contains("\"Ollama\"", f.U.Confirms[^1], StringComparison.Ordinal);

        f.U.ConfirmResult = true;
        await f.Vm.DeleteProviderAsync(f.Vm.Providers[1]);
        var saved = await f.ReloadAsync();
        Assert.Equal([claude.Id], saved.Providers.Select(p => p.Id));
        Assert.Equal(claude.Id, saved.DefaultProviderId);
        Assert.Null(await f.Service.GetApiKeyAsync(ollama, Ct));
        Assert.True(f.Vm.Providers.Single().IsDefault);

        // deleting the provider open in the editor closes the editor
        await f.Vm.EditProviderAsync(f.Vm.Providers[0]);
        await f.Vm.DeleteProviderAsync(f.Vm.Providers[0]);
        Assert.Null(f.Vm.Editor);
        Assert.False(f.Vm.HasProviders);
        Assert.Null((await f.ReloadAsync()).DefaultProviderId);
    }

    [Fact]
    public async Task Behaviour_settings_are_saved_immediately()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.True(f.Vm.RedactSecrets);
        Assert.Equal("200", f.Vm.TerminalLinesText);
        Assert.Equal("25", f.Vm.MaxStepsText);
        Assert.Null(f.Vm.SelectedRetention!.Days);
        Assert.True(f.Vm.CanDeleteConversations);

        f.Vm.RedactSecrets = false;
        f.Vm.AttachHostByDefault = false;
        f.Vm.SaveHistory = false;
        f.Vm.TerminalLinesText = "500";
        f.Vm.MaxStepsText = "10";
        f.Vm.SelectedRetention = f.Vm.RetentionOptions.Single(o => o.Days == 30);
        await f.Vm.PendingSave;
        var saved = await f.ReloadAsync();
        Assert.Equal((false, false, false, 500, 10, (int?)30), (saved.RedactSecrets, saved.AttachHostByDefault, saved.SaveHistory, saved.TerminalContextLines, saved.MaxSteps, saved.RetentionDays));

        foreach (var bad in new[] { "0", "2001", "abc", "" })
        {
            f.Vm.TerminalLinesText = bad;
            Assert.Equal("Enter a whole number from 1 to 2000.", f.Vm.TerminalLinesError);
        }
        f.Vm.MaxStepsText = "101";
        Assert.NotNull(f.Vm.MaxStepsError);
        await f.Vm.PendingSave;
        saved = await f.ReloadAsync();
        Assert.Equal((500, 10), (saved.TerminalContextLines, saved.MaxSteps));
        f.Vm.TerminalLinesText = "2000";
        f.Vm.MaxStepsText = "1";
        Assert.Null(f.Vm.TerminalLinesError);
        await f.Vm.PendingSave;
        saved = await f.ReloadAsync();
        Assert.Equal((2000, 1), (saved.TerminalContextLines, saved.MaxSteps));

        // a fresh view model shows what was stored
        using var again = new AiSettingsViewModel(f.Service, f.U, null);
        await again.LoadAsync(Ct);
        Assert.Equal((false, "2000", "1", (int?)30), (again.RedactSecrets, again.TerminalLinesText, again.MaxStepsText, again.SelectedRetention!.Days));
        Assert.False(again.CanDeleteConversations);
    }

    [Fact]
    public async Task Delete_all_conversations_asks_first()
    {
        await using var f = await Fixture.CreateAsync();
        var conversations = f.Store.Storage.AiConversations;
        await conversations.SaveAsync(new AiConversation { Title = "disk usage" }, Ct);
        await conversations.SaveAsync(new AiConversation { Title = "nginx" }, Ct);

        f.U.ConfirmResult = false;
        await f.Vm.DeleteAllConversationsAsync();
        Assert.Equal(2, (await conversations.ListAsync(Ct)).Count);

        f.U.ConfirmResult = true;
        await f.Vm.DeleteAllConversationsAsync();
        Assert.Empty(await conversations.ListAsync(Ct));
        Assert.Equal("Deleted 2 conversations.", f.Vm.Message);
    }

    [Fact]
    public async Task Strings_exist_in_all_languages()
    {
        await using var f = await Fixture.CreateAsync();
        foreach (var culture in Loc.SupportedCultures)
        {
            Loc.Instance.SetCulture(culture);
            Assert.NotEqual("aiSettings.tab", Loc.Instance["aiSettings.tab"]);
            Assert.NotEqual("aiSettings.validation.headerKey", Loc.Instance["aiSettings.validation.headerKey"]);
            Assert.DoesNotContain("{", new AiRetentionOption(7).Label, StringComparison.Ordinal);
        }
        Loc.Instance.SetCulture("zh-CN");
        Assert.Equal("AI 助手", Loc.Instance["aiSettings.tab"]);
        Loc.Instance.SetCulture("ja-JP");
        Assert.Equal("AI アシスタント", Loc.Instance["aiSettings.tab"]);
        Loc.Instance.SetCulture("en-US");
    }

    // ------------------------------------------------------------------ headless rendering

    [AvaloniaFact]
    public async Task Tab_renders_in_light_and_dark()
    {
        await using var f = await Fixture.CreateAsync();
        var page = new AiMcpPageHost(f.Vm);
        var w = new Window { Width = 1280, Height = 860, Content = page.View };
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        w.Show();
        Pump();
        Assert.True(w.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "ProvidersEmpty").IsEffectivelyVisible);
        Save(w, "p4-ai-settings-empty-light");
        f.Vm.TerminalLinesText = "0";
        var deleteAll = w.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "DeleteAllConversationsButton");
        Assert.True(deleteAll.IsEffectivelyVisible);
        deleteAll.BringIntoView();
        Save(w, "p4-ai-settings-behaviour-light");
        f.Vm.TerminalLinesText = "200";

        await AddClaudeAsync(f);
        await AddOllamaAsync(f);
        await f.Vm.AddProviderAsync(AiProviderKind.OpenAI);
        await f.Vm.Editor!.FetchModelsAsync();
        Pump();
        Assert.Equal(2, w.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ProviderRows").ItemCount);
        Save(w, "p4-ai-settings-light");

        await f.Vm.CloseEditorAsync();
        await f.Vm.EditProviderAsync(f.Vm.Providers[0]);
        f.P.Probe = new ProviderProbeResult(true, [new("claude-opus-5-5"), new("claude-sonnet-5")], new ProviderCapabilities());
        await f.Vm.Editor!.TestConnectionAsync();
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Pump();
        var note = w.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "TestSuccessNote");
        Assert.True(note.IsEffectivelyVisible);
        note.BringIntoView();
        Save(w, "p4-ai-settings-claude-dark");
        w.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "HeadersBox").BringIntoView();
        Save(w, "p4-ai-settings-claude-options-dark");

        await f.Vm.CloseEditorAsync();
        await f.Vm.AddProviderAsync(AiProviderKind.Custom);
        var e = f.Vm.Editor!;
        e.BaseUrl = "ftp://x";
        e.HeadersText = "Authorization: Bearer x";
        await f.Vm.SaveEditorAsync();
        Pump();
        Save(w, "p4-ai-settings-custom-errors-dark");

        await f.Vm.CloseEditorAsync();
        Loc.Instance.SetCulture("ja-JP");
        f.Vm.Refresh();
        Pump();
        Save(w, "p4-ai-settings-dark-ja");
        Loc.Instance.SetCulture("zh-CN");
        f.Vm.Refresh();
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        Pump();
        Save(w, "p4-ai-settings-light-zh");
        Loc.Instance.SetCulture("en-US");
        w.Close();
    }

    /// <summary>The settings view inside a page-like frame (heading + the same margins as AiMcpPage) so the screenshots
    /// show the real layout without the MCP controller and audit store the full page needs.</summary>
    sealed class AiMcpPageHost
    {
        public AiMcpPageHost(AiSettingsViewModel vm)
        {
            var tabs = new TabControl
            {
                ItemsSource = new[]
                {
                    new TabItem { Header = Loc.Instance["mcp.tab"] },
                    new TabItem { Header = Loc.Instance["audit.tab"] },
                    new TabItem { Header = Loc.Instance["aiSettings.tab"], Content = new AiSettingsView { DataContext = vm } },
                },
                SelectedIndex = 2,
            };
            var heading = new TextBlock { Text = Loc.Instance["nav.ai"], Margin = new Thickness(0, 0, 0, 16) };
            heading.Classes.Add("heading-04");
            DockPanel.SetDock(heading, Dock.Top);
            View = new DockPanel { Margin = new Thickness(32, 24, 32, 32), Children = { heading, tabs } };
        }

        public Control View { get; }
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
