namespace MySSH.Ai.Tests;

public sealed class OpenAIProbeTests : OpenAITestBase
{
    IEnumerable<string> Gets => Mock.Requests.Where(r => r.Method == "GET").Select(r => r.PathAndQuery);

    [Fact]
    public async Task Lists_models_on_the_path_below_the_base_url()
    {
        Mock.ModelsJson = OpenAIFixtures.Models;
        var a = await OpenAI().ListModelsAsync(Ct);
        var d = await DeepSeek().ListModelsAsync(Ct);
        Assert.Equal(["gpt-test", "deepseek-chat"], a.Select(m => m.Id));
        Assert.Equal(a.Select(m => m.Id), d.Select(m => m.Id));
        Assert.Equal(["/v1/models", "/models"], Gets);
        Assert.Equal("Bearer sk-test", Mock.Requests[0].Headers["authorization"]);
    }

    [Fact]
    public async Task Listing_errors_throw_provider_exceptions()
    {
        Mock.GetResponses.Enqueue(MockResponse.Json(OpenAIFixtures.Error("Incorrect API key provided"), 401));
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => OpenAI().ListModelsAsync(Ct));
        Assert.Equal((AiErrorKind.Authentication, 401), (ex.Kind, ex.Status));
        Assert.Contains("Incorrect API key provided", ex.Message, StringComparison.Ordinal);

        Mock.GetResponses.Enqueue(MockResponse.Json("<html>not here</html>", 404));
        var nf = await Assert.ThrowsAsync<AiProviderException>(() => Custom().ListModelsAsync(Ct));
        Assert.Equal(AiErrorKind.InvalidRequest, nf.Kind);
    }

    [Fact]
    public async Task Probe_reports_models_and_full_capabilities()
    {
        Mock.ModelsJson = OpenAIFixtures.Models;
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        var r = await OpenAI().ProbeAsync("gpt-test", Ct);

        Assert.True(r.Ok, r.Error);
        Assert.Null(r.Error);
        Assert.Equal(["gpt-test", "deepseek-chat"], r.Models.Select(m => m.Id));
        Assert.Equal((true, true), (r.Detected.Tools, r.Detected.Streaming));
        var b = Mock.Posts.Single().Json;
        Assert.Equal("gpt-test", b.GetProperty("model").GetString());
        Assert.Equal(1, b.GetProperty("tools").GetArrayLength());
        Assert.True(b.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Probe_tolerates_a_server_without_models()
    {
        Mock.GetResponses.Enqueue(MockResponse.Json("404 page not found", 404));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        var r = await Custom().ProbeAsync("llama3", Ct);
        Assert.True(r.Ok, r.Error);
        Assert.Empty(r.Models);
        Assert.Equal((true, true), (r.Detected.Tools, r.Detected.Streaming));
    }

    [Fact]
    public async Task Probe_downgrades_tools_when_the_server_rejects_them()
    {
        Mock.ModelsJson = OpenAIFixtures.Models;
        Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.Error("registry.ollama.ai/library/gemma:2b does not support tools"), 400));
        Mock.PostResponses.Enqueue(MockResponse.Sse(OpenAIFixtures.Final));
        var r = await Custom().ProbeAsync("gemma:2b", Ct);

        Assert.True(r.Ok, r.Error);
        Assert.Equal((false, true), (r.Detected.Tools, r.Detected.Streaming));
        Assert.True(Mock.Posts[0].Json.TryGetProperty("tools", out _));
        Assert.False(Mock.Posts[1].Json.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Probe_falls_back_to_non_streaming_when_streaming_fails()
    {
        Mock.ModelsJson = OpenAIFixtures.Models;
        // A server that ignores stream:true and answers with plain JSON: no SSE events at all.
        Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.NonStreaming));
        Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.NonStreaming));
        var r = await Custom().ProbeAsync("local-model", Ct);

        Assert.True(r.Ok, r.Error);
        Assert.Equal((true, false), (r.Detected.Tools, r.Detected.Streaming));
        Assert.True(Mock.Posts[0].Json.GetProperty("stream").GetBoolean());
        Assert.False(Mock.Posts[1].Json.TryGetProperty("stream", out var s) && s.GetBoolean());
    }

    [Fact]
    public async Task Probe_can_downgrade_both_tools_and_streaming()
    {
        Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.Error("streaming is not supported"), 400));
        Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.Error("tools are not supported by this model"), 400));
        Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.NonStreaming));
        Mock.ModelsJson = OpenAIFixtures.Models;
        var r = await Custom().ProbeAsync("local-model", Ct);

        Assert.True(r.Ok, r.Error);
        Assert.Equal((false, false), (r.Detected.Tools, r.Detected.Streaming));
        Assert.Equal(3, Mock.Posts.Count);
        Assert.False(Mock.Posts[2].Json.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Probe_stops_at_an_authentication_failure_and_reports_it()
    {
        Mock.GetResponses.Enqueue(MockResponse.Json(OpenAIFixtures.Error("Incorrect API key provided"), 401));
        var r = await OpenAI().ProbeAsync("gpt-test", Ct);
        Assert.False(r.Ok);
        Assert.Contains("Incorrect API key provided", r.Error, StringComparison.Ordinal);
        Assert.Empty(Mock.Posts);
    }

    [Fact]
    public async Task Probe_reports_an_unknown_model_without_throwing()
    {
        Mock.ModelsJson = OpenAIFixtures.Models;
        for (int i = 0; i < 2; i++) Mock.PostResponses.Enqueue(MockResponse.Json(OpenAIFixtures.Error("The model `gpt-nope` does not exist"), 404));
        var r = await OpenAI().ProbeAsync("gpt-nope", Ct);
        Assert.False(r.Ok);
        Assert.Contains("gpt-nope", r.Error, StringComparison.Ordinal);
        Assert.Equal(2, r.Models.Count);
        Assert.Equal(2, Mock.Posts.Count); // streaming, then non-streaming; no tool downgrade (message is not about tools)
    }
}
