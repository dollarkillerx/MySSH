using System.Net;
using System.Net.Sockets;
using MySSH.Ai.Providers.OpenAI;

namespace MySSH.Ai.Tests;

public sealed class OpenAIErrorTests : OpenAITestBase
{
    [Theory]
    [InlineData(400, AiErrorKind.InvalidRequest)]
    [InlineData(401, AiErrorKind.Authentication)]
    [InlineData(403, AiErrorKind.Authentication)]
    [InlineData(404, AiErrorKind.InvalidRequest)]
    [InlineData(422, AiErrorKind.InvalidRequest)]
    [InlineData(429, AiErrorKind.RateLimited)]
    [InlineData(500, AiErrorKind.Other)]
    [InlineData(503, AiErrorKind.Overloaded)]
    public async Task Http_status_maps_to_error_kind_with_the_server_message(int status, AiErrorKind kind)
    {
        Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.Error("boom happened"), status));
        var events = await Collect(OpenAI(), Req("gpt-test", null, AiMessage.User("x")));
        var stop = Assert.IsType<Stopped>(Assert.Single(events));
        Assert.Equal((StopReason.Error, kind), (stop.Reason, stop.ErrorKind));
        Assert.Null(stop.Message);
        Assert.Contains($"HTTP {status}", stop.Detail, StringComparison.Ordinal);
        Assert.Contains("boom happened", stop.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Headers", stop.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_streaming_errors_and_ollama_style_error_bodies_are_mapped()
    {
        Mock.PostResponses.Enqueue(MockResponse.Json("""{"error":"model 'nope' not found"}""", 404));
        var stop = Stop(await Collect(OpenAI(new ProviderCapabilities(Streaming: false)), Req("nope", null, AiMessage.User("x"))));
        Assert.Equal((StopReason.Error, AiErrorKind.InvalidRequest), (stop.Reason, stop.ErrorKind));
        Assert.Equal("HTTP 404: model 'nope' not found", stop.Detail);
    }

    [Fact]
    public async Task Unreachable_server_is_a_network_error_for_stream_and_models()
    {
        var p = new OpenAICompatibleProvider(AiProviderConfig.OpenAI() with { BaseUrl = $"http://127.0.0.1:{ClosedPort()}/v1" }, "k", Http, NoRetry);
        var stop = Stop(await Collect(p, Req("m", null, AiMessage.User("x"))));
        Assert.Equal((StopReason.Error, AiErrorKind.Network), (stop.Reason, stop.ErrorKind));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => p.ListModelsAsync(Ct));
        Assert.Equal(AiErrorKind.Network, ex.Kind);

        var probe = await p.ProbeAsync("m", Ct);
        Assert.False(probe.Ok);
        Assert.NotNull(probe.Error);
    }

    [Fact]
    public async Task Invalid_base_url_is_reported_not_thrown()
    {
        var p = new OpenAICompatibleProvider(AiProviderConfig.Custom(AiProtocol.OpenAICompatible, "localhost:11434"), null, Http);
        var stop = Stop(await Collect(p, Req("m", null, AiMessage.User("x"))));
        Assert.Equal((StopReason.Error, AiErrorKind.InvalidRequest), (stop.Reason, stop.ErrorKind));
        await Assert.ThrowsAsync<AiProviderException>(() => p.ListModelsAsync(Ct));
        Assert.False((await p.ProbeAsync("m", Ct)).Ok);
    }

    [Fact]
    public async Task Retries_429_and_5xx_honouring_retry_after_ms()
    {
        var options = new OpenAIProviderOptions { MaxRetries = 2, RetryBaseDelay = TimeSpan.FromMilliseconds(10) };
        Mock.PostResponses.Enqueue(new MockResponse(429, "application/json", OpenAIFixtures.Error("slow down", "rate_limit"),
            Headers: new Dictionary<string, string> { ["retry-after-ms"] = "5" }));
        Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.Error("oops", "server_error"), 500));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        var stop = Stop(await Collect(OpenAI(options: options), Req("m", null, AiMessage.User("x"))));
        Assert.Equal(StopReason.EndTurn, stop.Reason);
        Assert.Equal(3, Mock.Posts.Count);
    }

    [Fact]
    public async Task Gives_up_after_the_configured_retries_and_never_retries_4xx()
    {
        var options = new OpenAIProviderOptions { MaxRetries = 2, RetryBaseDelay = TimeSpan.FromMilliseconds(5) };
        for (int i = 0; i < 3; i++) Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.Error("busy"), 503));
        var p = OpenAI(options: options);
        var stop = Stop(await Collect(p, Req("m", null, AiMessage.User("x"))));
        Assert.Equal(AiErrorKind.Overloaded, stop.ErrorKind);
        Assert.Equal(3, Mock.Posts.Count);

        Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.Error("bad key"), 401));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        Assert.Equal(AiErrorKind.Authentication, Stop(await Collect(p, Req("m", null, AiMessage.User("x")))).ErrorKind);
        Assert.Equal(4, Mock.Posts.Count);
    }

    [Fact]
    public async Task Idle_stream_times_out_as_a_network_error()
    {
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Slow) with { ChunkDelayMs = 1500 });
        var p = OpenAI(options: NoRetry with { NetworkTimeout = TimeSpan.FromMilliseconds(400) });
        var events = await Collect(p, Req("m", null, AiMessage.User("x")));
        var stop = Stop(events);
        Assert.Equal((StopReason.Error, AiErrorKind.Network), (stop.Reason, stop.ErrorKind));
        Assert.Equal("one ", events.OfType<TextDelta>().Single().Text);
    }

    [Fact]
    public async Task Cancellation_throws_operation_canceled()
    {
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Slow) with { ChunkDelayMs = 300 });
        using var cts = new CancellationTokenSource();
        var p = OpenAI();
        var seen = new List<ChatEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in p.StreamAsync(Req("m", null, AiMessage.User("x")), cts.Token))
            {
                seen.Add(e);
                if (e is TextDelta) await cts.CancelAsync();
            }
        });
        Assert.DoesNotContain(seen, e => e is Stopped);

        // Already cancelled: no request at all.
        var before = Mock.Posts.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectWith(p, Req("m", null, AiMessage.User("x")), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.ListModelsAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.ProbeAsync("m", cts.Token));
        Assert.Equal(before, Mock.Posts.Count);
    }

    [Fact]
    public async Task Cancellation_during_a_non_streaming_request_throws()
    {
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Slow) with { ChunkDelayMs = 2000 });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CollectWith(OpenAI(new ProviderCapabilities(Streaming: false)), Req("m", null, AiMessage.User("x")), cts.Token));
    }

    static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
