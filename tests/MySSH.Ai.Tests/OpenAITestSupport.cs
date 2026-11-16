using System.Text.Json;
using MySSH.Ai.Providers.OpenAI;

namespace MySSH.Ai.Tests;

/// <summary>Mock server + provider factories shared by the OpenAI-compatible provider tests.</summary>
public abstract class OpenAITestBase : IAsyncLifetime
{
    protected MockServer Mock { get; private set; } = null!;
    protected HttpClient Http { get; } = new();

    internal static readonly OpenAIProviderOptions NoRetry = new() { MaxRetries = 0 };

    public async ValueTask InitializeAsync() => Mock = await MockServer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Mock.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    internal OpenAICompatibleProvider OpenAI(ProviderCapabilities? caps = null, OpenAIProviderOptions? options = null,
        IReadOnlyDictionary<string, string>? headers = null, string? key = "sk-test", string baseSuffix = "/v1") =>
        new(AiProviderConfig.OpenAI() with
        {
            BaseUrl = Mock.BaseUrl + baseSuffix,
            Capabilities = caps ?? new(),
            ExtraHeaders = headers ?? new Dictionary<string, string>(),
        }, key, Http, options ?? NoRetry);

    internal OpenAICompatibleProvider DeepSeek(ProviderCapabilities? caps = null) =>
        new(AiProviderConfig.DeepSeek() with { BaseUrl = Mock.BaseUrl, Capabilities = caps ?? new() }, "ds-test", Http, NoRetry);

    internal OpenAICompatibleProvider Custom(string? key = null, IReadOnlyDictionary<string, string>? headers = null) =>
        new(AiProviderConfig.Custom(AiProtocol.OpenAICompatible, Mock.BaseUrl + "/v1") with
        {
            ExtraHeaders = headers ?? new Dictionary<string, string>(),
        }, key, Http, NoRetry);

    protected static readonly AiToolSpec[] Tools =
    [
        new("read_terminal", "Read the terminal.", Schema("""{"type":"object","properties":{"session":{"type":"string"},"lines":{"type":"integer"}}}""")),
        new("run_command", "Run a command.", Schema("""{"type":"object","properties":{"host":{"type":"string"},"command":{"type":"string"}},"required":["command"]}""")),
    ];

    protected static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    protected static ChatRequest Req(string model, IReadOnlyList<AiToolSpec>? tools, params AiMessage[] history) => new()
    {
        Model = model, SystemPrompt = "sys", History = history, Tools = tools ?? Tools, MaxTokens = 1000,
    };

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static Task<List<ChatEvent>> Collect(IChatProvider p, ChatRequest req) => CollectWith(p, req, Ct);

    protected static async Task<List<ChatEvent>> CollectWith(IChatProvider p, ChatRequest req, CancellationToken ct)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in p.StreamAsync(req, ct)) list.Add(e);
        return list;
    }

    protected static Stopped Stop(List<ChatEvent> events) => Assert.IsType<Stopped>(events[^1]);

    /// <summary>What the history store does: the message goes through a string column and comes back.</summary>
    protected static AiMessage ThroughStorage(AiMessage m) => m with
    {
        RawContent = m.RawContent is null ? null : new string(m.RawContent.AsSpan()),
        ToolCalls = [.. m.ToolCalls.Select(c => c with { Input = c.Input is { } i ? JsonDocument.Parse(i.GetRawText()).RootElement.Clone() : null })],
    };

    protected static string[] Roles(JsonElement messages) =>
        [.. messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()!)];
}
