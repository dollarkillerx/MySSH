namespace MySSH.Ai.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AnthropicEnvGroup { public const string Name = "anthropic-process-env"; }

/// <summary>
/// The spike found the Anthropic SDK reads ANTHROPIC_BASE_URL / API_KEY / AUTH_TOKEN / CUSTOM_HEADERS (FINDINGS §4). Our
/// client must not: hostile values are set in-process (pointing at a local trap server) and must have no effect.
/// </summary>
[Collection(AnthropicEnvGroup.Name)]
public sealed class AnthropicEnvTests : IAsyncLifetime
{
    MockServer _mock = null!;
    MockServer _trap = null!;
    readonly HttpClient _http = new();
    readonly Dictionary<string, string?> _saved = [];

    static readonly string[] Names =
    [
        "ANTHROPIC_BASE_URL", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_BETA",
        "ANTHROPIC_VERSION", "ANTHROPIC_MODEL",
    ];

    public async ValueTask InitializeAsync()
    {
        _mock = await MockServer.StartAsync();
        _trap = await MockServer.StartAsync();
        foreach (var n in Names) _saved[n] = Environment.GetEnvironmentVariable(n);
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", _trap.BaseUrl);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "env-api-key");
        Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "env-auth-token");
        Environment.SetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS", "X-Leaked-Header: from-env");
        Environment.SetEnvironmentVariable("ANTHROPIC_BETA", "env-beta");
        Environment.SetEnvironmentVariable("ANTHROPIC_VERSION", "1999-01-01");
        Environment.SetEnvironmentVariable("ANTHROPIC_MODEL", "env-model");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
        await _mock.DisposeAsync();
        await _trap.DisposeAsync();
        _http.Dispose();
    }

    IChatProvider Provider(string? key, bool bearer = false) =>
        AiProviders.Create(AiProviderConfig.Claude() with { BaseUrl = _mock.BaseUrl, UseBearerAuth = bearer }, key, _http);

    static ChatRequest Req() => new() { Model = "claude-opus-5-5", SystemPrompt = "s", History = [AiMessage.User("hi")] };

    static async Task<Stopped> Drain(IChatProvider p)
    {
        Stopped? last = null;
        await foreach (var e in p.StreamAsync(Req(), TestContext.Current.CancellationToken)) if (e is Stopped s) last = s;
        return last!;
    }

    void AssertClean(RecordedRequest r)
    {
        Assert.False(r.Headers.ContainsKey("x-leaked-header"));
        Assert.Equal("2026-10-21", r.Headers["anthropic-version"]);
        if (r.Headers.TryGetValue("anthropic-beta", out var beta)) Assert.DoesNotContain("env-beta", beta, StringComparison.Ordinal);
        Assert.Empty(_trap.Requests);
    }

    [Fact]
    public async Task Configured_key_and_base_url_are_used_and_env_is_ignored()
    {
        _mock.PostResponses.Enqueue(MockResponse.Sse(AnthropicFixtures.Final));
        Assert.Equal(StopReason.EndTurn, (await Drain(Provider("config-key"))).Reason);
        var r = Assert.Single(_mock.Posts);
        Assert.Equal("config-key", r.Headers["x-api-key"]);
        Assert.False(r.Headers.ContainsKey("authorization"));
        Assert.Equal("claude-opus-5-5", r.Json.GetProperty("model").GetString());
        AssertClean(r);

        _mock.ModelsJson = AnthropicFixtures.ModelsSinglePage;
        await Provider("config-key", bearer: true).ListModelsAsync(TestContext.Current.CancellationToken);
        var g = Assert.Single(_mock.Requests, x => x.Method == "GET");
        Assert.Equal("Bearer config-key", g.Headers["authorization"]);
        Assert.False(g.Headers.ContainsKey("x-api-key"));
        AssertClean(g);
    }

    [Fact]
    public async Task Missing_key_sends_no_credential_at_all()
    {
        _mock.PostResponses.Enqueue(MockResponse.Sse(AnthropicFixtures.Final));
        await Drain(Provider(null));
        var r = Assert.Single(_mock.Posts);
        Assert.False(r.Headers.ContainsKey("x-api-key"), "env ANTHROPIC_API_KEY leaked");
        Assert.False(r.Headers.ContainsKey("authorization"), "env ANTHROPIC_AUTH_TOKEN leaked");
        AssertClean(r);
    }
}
