using Anthropic;
using Anthropic.Models.Beta.Messages;
using AiSpike.Core;

namespace AiSpike.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvCollection { public const string Name = "process-env"; }

/// <summary>Temporarily overrides process env vars (in-process only) and restores them.</summary>
public sealed class EnvScope : IDisposable
{
    private readonly Dictionary<string, string?> _saved = new();
    public EnvScope(Dictionary<string, string?> vars)
    {
        foreach (var (k, v) in vars)
        {
            _saved[k] = Environment.GetEnvironmentVariable(k);
            Environment.SetEnvironmentVariable(k, v);
        }
    }
    public void Dispose()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
    }
}

/// <summary>
/// Does the Anthropic SDK read ANTHROPIC_* from the environment? (It does.) Every test points the env
/// at a local "trap" mock so nothing can reach the developer's real ANTHROPIC_BASE_URL.
/// </summary>
[Collection(EnvCollection.Name)]
public sealed class EnvPitfallTests : IAsyncLifetime
{
    private MockServer _mock = null!;
    private MockServer _trap = null!;
    private string _emptyConfigDir = null!;

    public async Task InitializeAsync()
    {
        _mock = await MockServer.StartAsync();
        _trap = await MockServer.StartAsync();
        _emptyConfigDir = Directory.CreateTempSubdirectory("anthropic-cfg-").FullName;
    }

    public async Task DisposeAsync()
    {
        await _mock.DisposeAsync();
        await _trap.DisposeAsync();
        Directory.Delete(_emptyConfigDir, true);
    }

    private Dictionary<string, string?> HostileEnv() => new()
    {
        ["ANTHROPIC_BASE_URL"] = _trap.BaseUrl,
        ["ANTHROPIC_API_KEY"] = "env-api-key",
        ["ANTHROPIC_AUTH_TOKEN"] = "env-auth-token",
        ["ANTHROPIC_CUSTOM_HEADERS"] = "X-Leaked-Header: from-env",
        ["ANTHROPIC_CONFIG_DIR"] = _emptyConfigDir, // isolate from the developer's `ant` profiles
        ["ANTHROPIC_PROFILE"] = null,
    };

    private static ChatRequest Req() => new()
    {
        Model = AnthropicProvider.DefaultModel, SystemPrompt = "s", History = [AiMessage.User("hi")],
    };

    private static async Task Drain(IChatProvider p)
    {
        await foreach (var _ in p.StreamAsync(Req(), default)) { }
    }

    [Fact]
    public async Task Default_constructed_client_silently_uses_env_base_url_and_key()
    {
        using var _ = new EnvScope(HostileEnv());
        _trap.PostResponses.Enqueue(MockResponse.Json("""
            {"id":"msg_t","type":"message","role":"assistant","model":"claude-opus-5-5","content":[{"type":"text","text":"ok"}],
             "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
            """));
        var client = new AnthropicClient { MaxRetries = 0 };
        var msg = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = "claude-opus-5-5", MaxTokens = 16, Messages = [new BetaUserMessageParam { Content = "hi" }],
        });
        Assert.Equal("msg_t", msg.ID);

        var hit = Assert.Single(_trap.Requests);
        Assert.Equal("Bearer env-api-key", hit.Headers["authorization"]);
        Assert.Equal("from-env", hit.Headers["x-leaked-header"]);
        Assert.Equal(_trap.BaseUrl.TrimEnd('/'), client.BaseUrl.TrimEnd('/'));
        Assert.Empty(_mock.Requests);
    }

    [Fact]
    public async Task Explicit_BaseUrl_and_ApiKey_win_over_env()
    {
        using var _ = new EnvScope(HostileEnv());
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicFinal));
        var provider = new AnthropicProvider(new AnthropicProviderOptions { BaseUrl = _mock.BaseUrl, ApiKey = "config-key", MaxRetries = 0 });
        await Drain(provider);

        Assert.Empty(_trap.Requests);
        var r = Assert.Single(_mock.Requests);
        Assert.Equal("Bearer config-key", r.Headers["authorization"]);
        Assert.False(r.Headers.ContainsKey("x-leaked-header"), "ANTHROPIC_CUSTOM_HEADERS leaked past StripEnvCustomHeadersHandler");
    }

    [Fact]
    public async Task Blank_key_maps_to_explicit_null_and_reads_no_env_credential()
    {
        using var _ = new EnvScope(HostileEnv());
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicFinal));
        var provider = new AnthropicProvider(new AnthropicProviderOptions { BaseUrl = _mock.BaseUrl, ApiKey = "", MaxRetries = 0 });
        await Drain(provider);

        var r = Assert.Single(_mock.Requests);
        Assert.False(r.Headers.ContainsKey("x-api-key"), "env ANTHROPIC_API_KEY leaked");
        Assert.False(r.Headers.ContainsKey("authorization"), "env credential leaked: " + r.Headers.GetValueOrDefault("authorization"));
        Assert.Empty(_trap.Requests);
    }

    [Fact]
    public async Task Blank_key_with_clean_env_still_sends_request_without_auth()
    {
        // Keyless Anthropic-compatible local gateway: SDK must not throw "missing credentials".
        var env = HostileEnv();
        env["ANTHROPIC_BASE_URL"] = null; env["ANTHROPIC_API_KEY"] = null; env["ANTHROPIC_AUTH_TOKEN"] = null; env["ANTHROPIC_CUSTOM_HEADERS"] = null;
        using var _ = new EnvScope(env);
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicFinal));
        await Drain(new AnthropicProvider(new AnthropicProviderOptions { BaseUrl = _mock.BaseUrl, ApiKey = null, MaxRetries = 0 }));
        var r = Assert.Single(_mock.Requests);
        Assert.False(r.Headers.ContainsKey("authorization"));
    }

    /// <summary>Raw SDK behaviour with the "obvious" explicit options (no isolation helpers).</summary>
    [Fact]
    public async Task Plain_AnthropicClient_with_explicit_options_still_leaks_headers_and_auth_token()
    {
        using var _ = new EnvScope(HostileEnv());
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicFinal));
        _mock.PostResponses.Enqueue(MockResponse.Sse(Fixtures.AnthropicFinal));

        var withKey = new AnthropicClient { BaseUrl = _mock.BaseUrl, ApiKey = "config-key", MaxRetries = 0 };
        var nullKey = new AnthropicClient { BaseUrl = _mock.BaseUrl, ApiKey = null, MaxRetries = 0 };
        foreach (var c in new[] { withKey, nullKey })
            await foreach (var __ in c.Beta.Messages.CreateStreaming(new MessageCreateParams
            {
                Model = "claude-opus-5-5", MaxTokens = 16, Messages = [new BetaUserMessageParam { Content = "hi" }],
            })) { }

        Assert.Empty(_trap.Requests);                          // explicit BaseUrl beats ANTHROPIC_BASE_URL
        var (a, b) = (_mock.Requests[0], _mock.Requests[1]);
        Assert.Equal("Bearer config-key", a.Headers["authorization"]);
        Assert.Equal("from-env", a.Headers["x-leaked-header"]);  // ANTHROPIC_CUSTOM_HEADERS merged anyway
        Assert.Equal("Bearer env-auth-token", b.Headers["authorization"]); // ApiKey=null => falls to AUTH_TOKEN
    }
}

// (no shared state)
