using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using MySSH.Ai;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.Desktop.Views;
using MySSH.Tools;

namespace MySSH.Desktop.Tests;

/// <summary>Scripted provider: each request consumes the next turn.</summary>
sealed class ScriptedChatProvider(params Func<ChatRequest, IEnumerable<ChatEvent>>[] turns) : IChatProvider
{
    readonly Queue<Func<ChatRequest, IEnumerable<ChatEvent>>> _turns = new(turns);
    public List<ChatRequest> Requests { get; } = [];
    public string ProviderId => "anthropic";
    public ProviderCapabilities Capabilities => new();
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ModelInfo>>([new("claude-opus-5-5")]);
    public Task<ProviderProbeResult> ProbeAsync(string model, CancellationToken ct) => Task.FromResult(new ProviderProbeResult(true, [], new()));

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        foreach (var e in _turns.Dequeue()(request))
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    public static IEnumerable<ChatEvent> Text(string text)
    {
        yield return new ThinkingDelta("Looking at the error. ");
        yield return new TextDelta(text);
        yield return new Usage(1200, 80, 0, 0);
        yield return new Stopped(StopReason.EndTurn, null, new AiMessage
        {
            Role = AiRole.Assistant, Text = text, Thinking = "Looking at the error. ", RawProvider = "anthropic", RawContent = "[]",
        });
    }

    public static IEnumerable<ChatEvent> Tool(string id, string name, string json)
    {
        var call = new ToolCall(id, name, JsonDocument.Parse(json).RootElement.Clone(), json);
        yield return new ToolCallStarted(id, name);
        yield return new ToolCallCompleted(call);
        yield return new Stopped(StopReason.ToolUse, null, new AiMessage { Role = AiRole.Assistant, ToolCalls = [call], RawProvider = "anthropic", RawContent = "[]" });
    }
}

/// <summary>Stands in for the real tool layer: asks for approval the way ToolInvoker does, through the panel.</summary>
sealed class ApprovingTools(Func<AiAssistantViewModel> assistant) : IToolInvoker
{
    public List<ApprovalOutcome> Outcomes { get; } = [];
    public static readonly Host Web = new() { Label = "web-01", Hostname = "10.0.1.11", AiPolicy = AiPolicy.Trusted };

    public async Task<ToolCallResult> InvokeAsync(ToolCallRequest request, ToolActor actor, CancellationToken ct = default)
    {
        var approval = new ApprovalRequest(Guid.NewGuid(), actor, new ToolCatalog().Find(request.ToolName)!, Web,
            request.Arguments.GetProperty("command").GetString()!, null, "high risk", true, DateTimeOffset.UtcNow.AddMinutes(2),
            MySSH.Tools.ApprovalReason.HighRisk, "systemctl restart");
        var pending = assistant().TryRequestApproval(approval, AiToolCallScope.Current!, ct) ?? throw new InvalidOperationException("no inline approval");
        var outcome = await pending;
        Outcomes.Add(outcome);
        return outcome == ApprovalOutcome.Approved ? new ToolCallResult(false, "exit_code: 0") : new ToolCallResult(true, "The user denied this call.");
    }
}

sealed class NoWorkspace : IAiWorkspace
{
    public TerminalSessionViewModel? CurrentTerminal => null;
}

sealed class OneTerminal(TerminalSessionViewModel terminal) : IAiWorkspace
{
    public TerminalSessionViewModel? CurrentTerminal => terminal;
}

sealed class FakeScreen : ITerminalScreen
{
    public List<(string Text, bool Enter)> Typed { get; } = [];
    public bool AcceptMultiline { get; set; }
    public string GetRecentText(int maxLines) => "$ uptime\n 10:00 up 3 days";
    public string? GetSelectionText() => null;
    public bool TypeText(string text, bool pressEnter)
    {
        if (!pressEnter && !AcceptMultiline && text.Contains('\n', StringComparison.Ordinal)) return false;
        Typed.Add((text, pressEnter));
        return true;
    }
}

sealed class UnusedConnector : MySSH.Ssh.ISshConnector
{
    public Task<MySSH.Ssh.ISshConnection> ConnectAsync(Guid hostId, ConnectionOrigin origin, CancellationToken ct = default) =>
        throw new InvalidOperationException("not used");
}

[Collection("Loc")]
public sealed class AiAssistantTests
{
    static async Task<(TestStore Store, AiSettingsService Settings, AiProviderConfig Provider)> SetupAsync(IChatProvider provider)
    {
        var t = await TestStore.OpenAsync();
        var settings = new AiSettingsService(t.Storage.Settings, t.Storage.Secrets) { ProviderFactory = (_, _, _) => provider };
        var claude = AiProviderConfig.Claude();
        await settings.SaveAsync(new AiSettings { Providers = [claude], DefaultProviderId = claude.Id });
        await settings.SetApiKeyAsync(claude, "sk-ant-test");
        return (t, settings, claude);
    }

    static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(done());
    }

    [AvaloniaFact]
    public async Task A_question_is_answered_saved_and_reopened()
    {
        Loc.Instance.SetCulture("en-US");
        var provider = new ScriptedChatProvider(_ => ScriptedChatProvider.Text("Port 80 is used by apache2."));
        var (t, settings, claude) = await SetupAsync(provider);
        await using var _ = t;
        var ui = new FakeUi();
        var ai = new AiAssistantViewModel(settings, t.Storage.AiConversations, t.Storage.Hosts, new ApprovingTools(() => null!), new ToolCatalog(), ui, new NoWorkspace());

        ai.Current.Input = "why did nginx fail? token=abcdefghijkl";
        await ai.Current.SendCommand.ExecuteAsync(null);

        Assert.Single(ui.Confirms); // data notice, once per provider
        Assert.Contains(claude.Id, settings.Current.DataNoticeAcknowledged);
        Assert.DoesNotContain("abcdefghijkl", provider.Requests[0].History[0].Text, StringComparison.Ordinal); // redacted
        var answer = Assert.Single(ai.Current.Items.OfType<AiAssistantItemViewModel>());
        Assert.Equal("Port 80 is used by apache2.", answer.Text);
        Assert.True(answer.HasThinking);
        Assert.Contains(ai.Current.Items, i => i is AiNoticeItemViewModel n && n.Text.Contains("Masked 1", StringComparison.Ordinal));
        Assert.Contains("1,200", ai.Current.UsageText, StringComparison.Ordinal);
        Assert.Equal(AiTools.FromCatalog(new ToolCatalog()).Select(x => x.Name), provider.Requests[0].Tools.Select(x => x.Name));

        await ai.LoadConversationsAsync();
        var saved = Assert.Single(ai.Conversations);
        Assert.StartsWith("why did nginx fail?", saved.Title, StringComparison.Ordinal);
        await ai.OpenConversationCommand.ExecuteAsync(saved);
        Assert.Equal(["why did nginx fail? token=[REDACTED]", "Port 80 is used by apache2."],
            ai.Current.Items.Select(i => i switch { AiUserItemViewModel u => u.Text, AiAssistantItemViewModel a => a.Text, _ => "" }).Where(s => s.Length > 0));
        Assert.Equal(2, ai.Current.History.Count);

        // A second question in the same conversation sends the whole history and asks no notice again.
        var follow = new ScriptedChatProvider(_ => ScriptedChatProvider.Text("Stop apache2 first."));
        settings.ProviderFactory = (_, _, _) => follow;
        ai.Current.Input = "how do I fix it?";
        await ai.Current.SendCommand.ExecuteAsync(null);
        Assert.Single(ui.Confirms);
        Assert.Equal(3, follow.Requests[0].History.Count);
        Assert.Equal(4, (await t.Storage.AiConversations.GetMessagesAsync(saved.Conversation.Id)).Count);
    }

    [AvaloniaFact]
    public async Task High_risk_tool_calls_wait_for_approval_on_their_card()
    {
        Loc.Instance.SetCulture("en-US");
        var provider = new ScriptedChatProvider(
            _ => ScriptedChatProvider.Tool("t1", "run_command", """{"host_id":"x","command":"sudo systemctl restart nginx"}"""),
            _ => ScriptedChatProvider.Tool("t2", "run_command", """{"host_id":"x","command":"sudo systemctl restart nginx"}"""),
            _ => ScriptedChatProvider.Text("nginx is running again."));
        var (t, settings, _) = await SetupAsync(provider);
        await using var __ = t;
        AiAssistantViewModel? ai = null;
        var tools = new ApprovingTools(() => ai!);
        ai = new AiAssistantViewModel(settings, t.Storage.AiConversations, t.Storage.Hosts, tools, new ToolCatalog(), new FakeUi(), new NoWorkspace());

        ai.Current.Input = "restart nginx";
        var send = ai.Current.SendCommand.ExecuteAsync(null);
        await Until(() => ai.Current.Items.OfType<AiToolCardViewModel>().Any(c => c.IsAwaitingApproval));
        var card = ai.Current.Items.OfType<AiToolCardViewModel>().Single();
        Assert.True(card.IsHighRisk);
        Assert.Equal("sudo systemctl restart nginx", card.Summary);
        Assert.Equal("web-01 (10.0.1.11)", card.HostLabel);
        Assert.Equal(MySSH.UI.Carbon.TagType.Red, card.StateTag);
        RenderPanel(ai, "40-ai-approval");
        card.AllowForSessionCommand.Execute(null);
        await send;

        // The same command again in this chat is approved without asking (the user chose "allow same in this chat").
        Assert.Equal([ApprovalOutcome.Approved, ApprovalOutcome.Approved], tools.Outcomes);
        Assert.All(ai.Current.Items.OfType<AiToolCardViewModel>(), c => Assert.Equal(AiToolCardState.Succeeded, c.State));
        Assert.Equal("nginx is running again.", ai.Current.Items.OfType<AiAssistantItemViewModel>().Last().Text);
        RenderPanel(ai, "41-ai-chat");
    }

    [AvaloniaFact]
    public async Task Stop_cancels_a_pending_approval_and_the_next_question_still_works()
    {
        Loc.Instance.SetCulture("en-US");
        var provider = new ScriptedChatProvider(
            _ => ScriptedChatProvider.Tool("t1", "run_command", """{"host_id":"x","command":"reboot"}"""),
            _ => ScriptedChatProvider.Text("ok"));
        var (t, settings, _) = await SetupAsync(provider);
        await using var __ = t;
        AiAssistantViewModel? ai = null;
        ai = new AiAssistantViewModel(settings, t.Storage.AiConversations, t.Storage.Hosts, new ApprovingTools(() => ai!), new ToolCatalog(), new FakeUi(), new NoWorkspace());
        ai.Current.Input = "reboot it";
        var send = ai.Current.SendCommand.ExecuteAsync(null);
        await Until(() => ai.Current.Items.OfType<AiToolCardViewModel>().Any(c => c.IsAwaitingApproval));
        ai.Current.StopCommand.Execute(null);
        await send;
        Assert.False(ai.Current.IsRunning);
        Assert.NotEqual(AiToolCardState.Succeeded, ai.Current.Items.OfType<AiToolCardViewModel>().Single().State);

        ai.Current.Input = "never mind";
        await ai.Current.SendCommand.ExecuteAsync(null);
        // The dangling tool call got an error result before the new question (valid history for the provider).
        var history = provider.Requests[1].History;
        Assert.Equal([AiRole.User, AiRole.Assistant, AiRole.ToolResults, AiRole.User], history.Select(m => m.Role));
    }

    [AvaloniaFact]
    public async Task A_host_with_AI_off_shows_its_policy_and_can_be_enabled_from_the_chat()
    {
        Loc.Instance.SetCulture("zh-CN");
        var provider = new ScriptedChatProvider(r =>
        {
            Assert.Contains("ai_policy: Off", r.History[^1].Text, StringComparison.Ordinal);
            return ScriptedChatProvider.Text("I can't access it yet.");
        }, r =>
        {
            Assert.Contains("ai_policy: ReadOnly", r.History[^1].Text, StringComparison.Ordinal);
            return ScriptedChatProvider.Text("ok");
        });
        var (t, settings, _) = await SetupAsync(provider);
        await using var __ = t;
        var host = new Host { Label = "203.0.113.10", Hostname = "203.0.113.10", Username = "root" };
        await t.Storage.Hosts.SaveAsync(host);
        var term = new TerminalSessionViewModel(new UnusedConnector(), new FakeUi(), host.Id, host.Label, "—") { Screen = new FakeScreen() };
        var ai = new AiAssistantViewModel(settings, t.Storage.AiConversations, t.Storage.Hosts, new ApprovingTools(() => null!), new ToolCatalog(), new FakeUi(), new OneTerminal(term));
        var reloads = 0;
        ai.HostsChanged += () => reloads++;
        await Until(() => ai.Current.HasChips);
        Assert.Equal("203.0.113.10 · AI 已关闭", ai.Current.Chips.Single().Label);
        var card = Assert.Single(ai.Current.Items.OfType<AiPolicyCardViewModel>());

        ai.Current.Input = "看看这台服务器的配置";
        await ai.Current.SendCommand.ExecuteAsync(null);
        RenderPanel(ai, "42-ai-policy-off");

        await card.EnableReadOnlyCommand.ExecuteAsync(null);
        Assert.True(card.IsDone);
        Assert.Equal(AiPolicy.ReadOnly, (await t.Storage.Hosts.GetAsync(host.Id))!.AiPolicy);
        Assert.Equal(1, reloads);
        await Until(() => ai.Current.Chips.Single().Label == "203.0.113.10 · AI：只读");
        ai.Current.Input = "再看看";
        await ai.Current.SendCommand.ExecuteAsync(null);
        Assert.Equal(2, provider.Requests.Count);
        Loc.Instance.SetCulture("en-US");
    }

    [AvaloniaFact]
    public async Task Code_blocks_insert_into_the_terminal_without_running()
    {
        Loc.Instance.SetCulture("en-US");
        var (t, settings, _) = await SetupAsync(new ScriptedChatProvider());
        await using var __ = t;
        var screen = new FakeScreen();
        var term = new TerminalSessionViewModel(new UnusedConnector(), new FakeUi(), Guid.NewGuid(), "web", "—") { Screen = screen };
        var ai = new AiAssistantViewModel(settings, t.Storage.AiConversations, t.Storage.Hosts, new ApprovingTools(() => null!), new ToolCatalog(), new FakeUi(), new OneTerminal(term));

        ai.InsertCodeCommand.Execute("df -h");
        Assert.Empty(screen.Typed); // not connected yet
        Assert.Contains(ai.Current.Items, i => i is AiNoticeItemViewModel n && n.Text.Contains("connected terminal", StringComparison.Ordinal));
        term.State = SessionState.Connected;
        ai.InsertCodeCommand.Execute("df -h\n");
        Assert.Equal([("df -h", false)], screen.Typed);
        ai.InsertCodeCommand.Execute("a\nb");
        Assert.Contains(ai.Current.Items, i => i is AiNoticeItemViewModel n && n.Text.Contains("multi-line", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Answers_render_as_markdown_in_the_panel()
    {
        Loc.Instance.SetCulture("zh-CN");
        var provider = new ScriptedChatProvider(_ => ScriptedChatProvider.Text(
            "**203.0.113.10 配置概览**\n\n| 项目 | 情况 |\n|---|---|\n| 系统 | Rocky Linux 9.3 (Blue Onyx)，内核 5.14.0-362 |\n| 内存 | 1.7 GiB，已用 335 MiB；无 **swap** |\n| 网络 | eth0：203.0.113.10/24，IPv6 `2001:db8::3e/64` |\n\n这台机器的 **AI 策略还没有设置**，`list_hosts` 返回空。\n\n你也可以手动运行：\n\n```bash\nuname -a; free -h\ndf -hT | grep -v tmpfs\n```\n\n1. 打开主机设置\n2. 设为 *只读*"));
        var (t, settings, _) = await SetupAsync(provider);
        await using var __ = t;
        var ai = new AiAssistantViewModel(settings, t.Storage.AiConversations, t.Storage.Hosts, new ApprovingTools(() => null!), new ToolCatalog(), new FakeUi(), new NoWorkspace());
        ai.Current.Input = "看看配置";
        await ai.Current.SendCommand.ExecuteAsync(null);
        RenderPanel(ai, "43-ai-markdown");
        Loc.Instance.SetCulture("en-US");
    }

    [Fact]
    public void Stored_user_text_is_shown_without_the_context_block()
    {
        var composed = AiContextComposer.Compose("why?", [new AiContextItem(AiContextKind.Terminal, "web-01 · last 200 lines", "$ x")], false);
        var (question, labels) = AiChatViewModel.SplitContext(composed.Text);
        Assert.Equal("why?", question);
        Assert.Equal(["web-01 · last 200 lines"], labels);
    }

    static void RenderPanel(AiAssistantViewModel ai, string name)
    {
        foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var w = new Window { Width = 460, Height = 760, Content = new AiAssistantPanel { DataContext = ai } };
            w.Show();
            MainWindowUiTestsHelpers.Save(w, $"{name}-{suffix}");
            w.Close();
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    }
}
