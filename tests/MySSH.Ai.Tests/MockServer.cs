using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MySSH.Ai.Tests;

public sealed record RecordedRequest(string Method, string PathAndQuery, Dictionary<string, string> Headers, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

/// <param name="Headers">Extra response headers (retry-after, request-id, ...).</param>
public sealed record MockResponse(int Status, string ContentType, string Body, int ChunkDelayMs = 0, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static MockResponse Sse(string body) => new(200, "text/event-stream", body);
    public static MockResponse Json(string body, int status = 200) => new(status, "application/json", body);
}

/// <summary>
/// Local Kestrel on 127.0.0.1:0. POSTs are answered from a FIFO queue of scripted responses
/// (SSE bodies written line by line with flushes); GET .../models returns <see cref="ModelsJson"/>.
/// </summary>
public sealed class MockServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public ConcurrentQueue<MockResponse> PostResponses { get; } = new();
    public List<RecordedRequest> Requests { get; } = [];
    public string ModelsJson { get; set; } = "{}";
    /// <summary>When non-empty, GET .../models answers from this queue first (paging, errors).</summary>
    public ConcurrentQueue<MockResponse> GetResponses { get; } = new();
    public string BaseUrl { get; }

    private MockServer(WebApplication app, string baseUrl) { _app = app; BaseUrl = baseUrl; }

    public static async Task<MockServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        MockServer? self = null;
        app.Run(async ctx => await self!.HandleAsync(ctx));
        await app.StartAsync();
        var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new MockServer(app, addr.TrimEnd('/'));
        return self;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var rec = new RecordedRequest(ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString,
            ctx.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString()), body);
        lock (Requests) Requests.Add(rec);

        if (HttpMethods.IsGet(ctx.Request.Method) && GetResponses.TryDequeue(out var get))
        {
            ctx.Response.StatusCode = get.Status;
            ctx.Response.ContentType = get.ContentType;
            await ctx.Response.WriteAsync(get.Body);
            return;
        }
        if (HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path.Value!.EndsWith("/models"))
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(ModelsJson);
            return;
        }
        if (!PostResponses.TryDequeue(out var resp))
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsync("""{"type":"error","error":{"type":"api_error","message":"mock: no scripted response"}}""");
            return;
        }
        ctx.Response.StatusCode = resp.Status;
        ctx.Response.ContentType = resp.ContentType;
        foreach (var (k, v) in resp.Headers ?? new Dictionary<string, string>()) ctx.Response.Headers[k] = v;
        if (resp.ContentType != "text/event-stream")
        {
            await ctx.Response.WriteAsync(resp.Body);
            return;
        }
        // Write SSE event by event (blank-line separated) to exercise incremental parsing.
        foreach (var chunk in resp.Body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            await ctx.Response.WriteAsync(chunk.Trim('\n') + "\n\n", Encoding.UTF8, ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            if (resp.ChunkDelayMs > 0) await Task.Delay(resp.ChunkDelayMs, ctx.RequestAborted);
        }
    }

    public IReadOnlyList<RecordedRequest> Posts { get { lock (Requests) return Requests.Where(r => r.Method == "POST").ToList(); } }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
