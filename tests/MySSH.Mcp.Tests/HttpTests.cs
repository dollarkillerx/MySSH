using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MySSH.Mcp.Tests;

public sealed class HttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(TestHost.Latest)]
    [InlineData(TestHost.Legacy)]
    public async Task ToolsList_exposes_catalog_names_schemas_and_annotations(string protocol)
    {
        await using var t = await TestHost.StartAsync(local: false);
        var client = await t.HttpClientAsync(protocol);
        Assert.Equal(protocol, client.NegotiatedProtocolVersion);
        Assert.Null(client.SessionId); // stateless

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        Assert.Equal(FakeCatalog.Instance.Tools.Select(d => d.Name), tools.Select(x => x.Name));
        foreach (var def in FakeCatalog.Instance.Tools)
        {
            var tool = tools.Single(x => x.Name == def.Name).ProtocolTool;
            Assert.Equal(def.Title, tool.Title);
            Assert.Equal(def.Description, tool.Description);
            Assert.True(JsonElement.DeepEquals(def.InputSchema, tool.InputSchema), $"{def.Name} schema: {tool.InputSchema.GetRawText()}");
            Assert.NotNull(tool.Annotations);
            Assert.Equal(def.ReadOnly, tool.Annotations.ReadOnlyHint);
            Assert.Equal(def.Destructive, tool.Annotations.DestructiveHint);
            Assert.Equal(def.Idempotent, tool.Annotations.IdempotentHint);
            Assert.Equal(def.OpenWorld, tool.Annotations.OpenWorldHint);
            Assert.Equal(def.Title, tool.Annotations.Title);
        }
        Assert.Equal("myssh", client.ServerInfo?.Name);
    }

    [Theory]
    [InlineData(TestHost.Latest, "mcp:xunit-client", "xunit-client (MCP)")]
    [InlineData(TestHost.Legacy, "mcp:http", "HTTP client (MCP)")] // G4: stateless + 2025-11-25 has no clientInfo per request
    public async Task CallTool_routes_arguments_result_and_actor(string protocol, string label, string displayName)
    {
        await using var t = await TestHost.StartAsync(local: false);
        var client = await t.HttpClientAsync(protocol);

        var r = await client.CallToolAsync("run_command", new Dictionary<string, object?> { ["host"] = "web-01", ["command"] = "uptime", ["timeout_s"] = 5 }, cancellationToken: Ct);
        Assert.NotEqual(true, r.IsError);
        Assert.Equal($"[fake] web-01$ uptime by {label}", Assert.IsType<TextContentBlock>(Assert.Single(r.Content)).Text);

        var call = Assert.Single(t.Invoker.Calls);
        Assert.Equal("run_command", call.Tool);
        Assert.Equal("web-01", call.Arguments.GetProperty("host").GetString());
        Assert.Equal(5, call.Arguments.GetProperty("timeout_s").GetInt32());
        Assert.True(call.Actor.IsMcp());
        Assert.Equal(label, call.Actor.Label);
        Assert.Equal(displayName, call.Actor.DisplayName);
    }

    [Fact]
    public async Task CallTool_maps_IsError_structured_content_and_invoker_exceptions()
    {
        await using var t = await TestHost.StartAsync(local: false);
        var client = await t.HttpClientAsync();

        var err = await client.CallToolAsync("run_command", new Dictionary<string, object?> { ["host"] = "nope", ["command"] = "x" }, cancellationToken: Ct);
        Assert.True(err.IsError);
        Assert.Equal("unknown host 'nope'", Assert.IsType<TextContentBlock>(Assert.Single(err.Content)).Text);

        var hosts = await client.CallToolAsync("list_hosts", cancellationToken: Ct);
        Assert.NotEqual(true, hosts.IsError);
        Assert.NotNull(hosts.StructuredContent);
        Assert.Equal("web-01", hosts.StructuredContent.Value.GetProperty("hosts")[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, Assert.Single(t.Invoker.Calls, c => c.Tool == "list_hosts").Arguments.ValueKind);

        var boom = await client.CallToolAsync("boom", cancellationToken: Ct);
        Assert.True(boom.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(boom.Content)).Text;
        Assert.Contains("internal error", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_unknown_tool_is_a_protocol_error()
    {
        await using var t = await TestHost.StartAsync(local: false);
        var client = await t.HttpClientAsync();
        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () => await client.CallToolAsync("rm_rf", cancellationToken: Ct));
        Assert.Contains("rm_rf", ex.Message, StringComparison.Ordinal);
        Assert.Empty(t.Invoker.Calls);
    }

    // ------------------------------------------------------------------ security (403 -> 421 -> 401)

    private static async Task<HttpResponseMessage> RawPost(TestHost t, string? auth, string? origin = null, string? host = null)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, t.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"raw","version":"1"}}}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (auth is not null) req.Headers.TryAddWithoutValidation("Authorization", auth);
        if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin);
        if (host is not null) req.Headers.Host = host;
        return await http.SendAsync(req, Ct);
    }

    [Fact]
    public async Task Http_rejects_missing_or_wrong_token_with_401()
    {
        await using var t = await TestHost.StartAsync(local: false);
        using var missing = await RawPost(t, null);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("Bearer realm=\"myssh\"", missing.Headers.WwwAuthenticate.ToString());
        using var wrong = await RawPost(t, "Bearer nope");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var noScheme = await RawPost(t, t.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, noScheme.StatusCode);
        using var ok = await RawPost(t, "Bearer " + t.Token);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(ok.Headers.Contains("Mcp-Session-Id"));
    }

    [Theory]
    [InlineData(TestHost.Latest)]
    [InlineData(TestHost.Legacy)]
    public async Task Http_sdk_client_with_wrong_token_fails(string protocol)
    {
        await using var t = await TestHost.StartAsync(local: false);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => t.HttpClientAsync(protocol, token: "nope"));
        Assert.True(ex.ToString().Contains("401", StringComparison.Ordinal) || ex.ToString().Contains("Unauthorized", StringComparison.Ordinal), ex.ToString());
        Assert.Empty(t.Invoker.Calls);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("file://localhost")]
    [InlineData("http://user@localhost")]
    public async Task Http_rejects_foreign_origin_with_403_before_auth(string origin)
    {
        await using var t = await TestHost.StartAsync(local: false);
        using var withToken = await RawPost(t, "Bearer " + t.Token, origin);
        Assert.Equal(HttpStatusCode.Forbidden, withToken.StatusCode);
        using var withoutToken = await RawPost(t, null, origin);
        Assert.Equal(HttpStatusCode.Forbidden, withoutToken.StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1")]
    [InlineData("https://[::1]:8443")]
    public async Task Http_accepts_loopback_origins(string origin)
    {
        await using var t = await TestHost.StartAsync(local: false);
        using var r = await RawPost(t, "Bearer " + t.Token, origin);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Http_rejects_rebinding_host_header_with_421_before_auth()
    {
        await using var t = await TestHost.StartAsync(local: false);
        using var r = await RawPost(t, "Bearer " + t.Token, host: "attacker.example:" + t.Endpoint.Port);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, r.StatusCode);
        using var noToken = await RawPost(t, null, host: "attacker.example");
        Assert.Equal(HttpStatusCode.MisdirectedRequest, noToken.StatusCode);
        using var localhost = await RawPost(t, "Bearer " + t.Token, host: "localhost:" + t.Endpoint.Port);
        Assert.Equal(HttpStatusCode.OK, localhost.StatusCode);
    }

    [Fact]
    public async Task SetToken_rotates_the_bearer_token_without_restart()
    {
        await using var t = await TestHost.StartAsync(local: false);
        var old = t.Token;
        var fresh = McpToken.Generate();
        t.Host.SetToken(fresh);
        Assert.Equal(fresh, t.Host.Token);
        using var r1 = await RawPost(t, "Bearer " + old);
        Assert.Equal(HttpStatusCode.Unauthorized, r1.StatusCode);
        using var r2 = await RawPost(t, "Bearer " + fresh);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

    [Fact]
    public async Task Http_binds_loopback_only_and_reports_port()
    {
        await using var t = await TestHost.StartAsync(local: false);
        Assert.Equal("127.0.0.1", t.Endpoint.Host);
        Assert.Equal("/mcp", t.Endpoint.AbsolutePath);
        Assert.Equal(t.Endpoint.Port, t.Host.Port);
        Assert.InRange(t.Endpoint.Port, 1, 65535);
        Assert.Null(t.Host.LocalSocketPath);
    }

    [Fact]
    public async Task Http_fixed_port_is_honoured()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        await using var host = await McpServerHost.StartAsync(new McpHostOptions { Port = port, EnableLocalSocket = false }, FakeCatalog.Instance, new FakeInvoker(), ct: Ct);
        Assert.Equal(port, host.Port);
        Assert.Equal(new Uri($"http://127.0.0.1:{port}/mcp"), host.HttpEndpoint);
        Assert.Equal(43, host.Token.Length); // generated when the options carry none
    }

    // ------------------------------------------------------------------ cancellation

    [Theory]
    [InlineData(TestHost.Latest, false)]
    [InlineData(TestHost.Legacy, false)]
    [InlineData(TestHost.Latest, true)]
    [InlineData(TestHost.Legacy, true)]
    public async Task Client_cancellation_reaches_the_invoker(string protocol, bool disposeClient)
    {
        await using var t = await TestHost.StartAsync(local: false);
        var client = await t.HttpClientAsync(protocol);
        var marker = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        var call = client.CallToolAsync("slow", new Dictionary<string, object?> { ["marker"] = marker }, cancellationToken: cts.Token).AsTask();
        await t.Invoker.StartedFor(marker).Task.WaitAsync(Wait.Timeout, Ct);

        if (disposeClient) await client.DisposeAsync();
        else await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => call);
        await t.Invoker.CancelledFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
        Assert.False(t.Invoker.Completed.ContainsKey(marker));
    }

    // ------------------------------------------------------------------ resources & prompts

    [Theory]
    [InlineData(TestHost.Latest)]
    [InlineData(TestHost.Legacy)]
    public async Task Resources_are_read_through_the_invoker(string protocol)
    {
        await using var t = await TestHost.StartAsync(local: false);
        var client = await t.HttpClientAsync(protocol);

        var resources = await client.ListResourcesAsync(cancellationToken: Ct);
        Assert.Equal("myssh://hosts", Assert.Single(resources).Uri);
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: Ct);
        Assert.Equal("myssh://sessions/{id}/screen", Assert.Single(templates).UriTemplate);

        var hosts = await client.ReadResourceAsync("myssh://hosts", cancellationToken: Ct);
        var hostsText = Assert.IsType<TextResourceContents>(Assert.Single(hosts.Contents));
        Assert.Equal("application/json", hostsText.MimeType);
        Assert.Equal("db-01", JsonDocument.Parse(hostsText.Text).RootElement.GetProperty("hosts")[1].GetProperty("name").GetString());

        var screen = await client.ReadResourceAsync("myssh://sessions/tab-7/screen", cancellationToken: Ct);
        Assert.Equal("screen of tab-7\n$ ", Assert.IsType<TextResourceContents>(Assert.Single(screen.Contents)).Text);

        var calls = t.Invoker.Calls.ToArray();
        Assert.Equal(["list_hosts", "read_terminal"], calls.Select(c => c.Tool));
        Assert.Equal("tab-7", calls[1].Arguments.GetProperty("session").GetString());
        Assert.All(calls, c => Assert.True(c.Actor.IsMcp()));

        await Assert.ThrowsAsync<McpProtocolException>(async () => await client.ReadResourceAsync("myssh://sessions/gone/screen", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpProtocolException>(async () => await client.ReadResourceAsync("myssh://secrets", cancellationToken: Ct));
    }

    [Fact]
    public async Task Prompts_are_listed_and_rendered()
    {
        await using var t = await TestHost.StartAsync(local: false);
        var client = await t.HttpClientAsync();

        var prompts = await client.ListPromptsAsync(cancellationToken: Ct);
        Assert.Equal(["diagnose_host", "analyze_logs"], prompts.Select(p => p.Name));
        Assert.True(prompts.Single(p => p.Name == "analyze_logs").ProtocolPrompt.Arguments!.All(a => a.Required == true));

        var diag = await client.GetPromptAsync("diagnose_host", new Dictionary<string, object?> { ["host"] = "web-01" }, cancellationToken: Ct);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(diag.Messages).Content).Text;
        Assert.Contains("\"web-01\"", text, StringComparison.Ordinal);
        Assert.Contains("list_hosts", text, StringComparison.Ordinal);
        Assert.Equal(Role.User, diag.Messages[0].Role);

        var logs = await client.GetPromptAsync("analyze_logs", new Dictionary<string, object?> { ["host"] = "web-01", ["path"] = "/var/log/syslog" }, cancellationToken: Ct);
        Assert.Contains("/var/log/syslog", Assert.IsType<TextContentBlock>(Assert.Single(logs.Messages).Content).Text, StringComparison.Ordinal);

        await Assert.ThrowsAsync<McpProtocolException>(async () => await client.GetPromptAsync("analyze_logs", new Dictionary<string, object?> { ["host"] = "web-01" }, cancellationToken: Ct));
        await Assert.ThrowsAsync<McpProtocolException>(async () => await client.GetPromptAsync("nope", cancellationToken: Ct));
        Assert.Empty(t.Invoker.Calls);
    }

    // ------------------------------------------------------------------ client tracking

    [Fact]
    public async Task Clients_track_http_callers_by_name_with_call_counts()
    {
        await using var t = await TestHost.StartAsync(local: false);
        var changes = 0;
        t.Host.ClientsChanged += () => Interlocked.Increment(ref changes);

        var named = await t.HttpClientAsync(TestHost.Latest, name: "claude-code");
        await named.CallToolAsync("list_hosts", cancellationToken: Ct);
        await named.CallToolAsync("list_hosts", cancellationToken: Ct);
        var legacy = await t.HttpClientAsync(TestHost.Legacy, name: "old-client");
        await legacy.CallToolAsync("list_hosts", cancellationToken: Ct);

        var cc = Assert.Single(t.Host.Clients, c => c.Id == "http:claude-code");
        Assert.Equal(McpClientTransport.Http, cc.Transport);
        Assert.Equal("claude-code", cc.Name);
        Assert.Equal("1.2.3", cc.Version);
        Assert.Equal(2, cc.CallCount);
        Assert.Equal(0, cc.InFlightCalls);
        Assert.False(cc.IsConnected);
        Assert.Equal("claude-code (MCP)", cc.DisplayName);
        Assert.True(cc.LastSeen >= cc.FirstSeen);
        var anon = Assert.Single(t.Host.Clients, c => c.Id == "http");
        Assert.Null(anon.Name);
        Assert.Equal(1, anon.CallCount);
        Assert.True(changes > 0);

        // in-flight while a call runs
        var marker = Guid.NewGuid().ToString("N");
        var slow = named.CallToolAsync("slow", new Dictionary<string, object?> { ["marker"] = marker, ["ms"] = 400 }, cancellationToken: Ct).AsTask();
        await t.Invoker.StartedFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
        Assert.Equal(1, t.Host.Clients.Single(c => c.Id == "http:claude-code").InFlightCalls);
        await slow;
        Assert.Equal(0, t.Host.Clients.Single(c => c.Id == "http:claude-code").InFlightCalls);

        // stateless HTTP: nothing to disconnect; the entry is forgotten
        Assert.False(await t.Host.DisconnectAsync("http:claude-code"));
        Assert.DoesNotContain(t.Host.Clients, c => c.Id == "http:claude-code");
        Assert.False(await t.Host.DisconnectAsync("no-such-client"));
    }

    [Fact]
    public async Task Client_names_are_sanitized_before_reaching_audit_and_prompts()
    {
        await using var t = await TestHost.StartAsync(local: false);
        var client = await t.HttpClientAsync(TestHost.Latest, name: "evil\u001b[31m\nname<script>" + new string('x', 200));
        await client.CallToolAsync("list_hosts", cancellationToken: Ct);
        var actor = Assert.Single(t.Invoker.Calls).Actor;
        Assert.StartsWith("mcp:evil__31m_name_script_x", actor.Label, StringComparison.Ordinal);
        Assert.True(actor.Label.Length <= "mcp:".Length + 64);
        Assert.DoesNotContain(actor.DisplayName, ch => char.IsControl(ch) || ch is '<' or '>');
    }

    [Fact]
    public async Task Concurrent_http_calls_run_in_parallel()
    {
        await using var t = await TestHost.StartAsync(local: false);
        var a = await t.HttpClientAsync();
        var b = await t.HttpClientAsync(TestHost.Legacy);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var calls = Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? a : b)
            .CallToolAsync("slow", new Dictionary<string, object?> { ["marker"] = $"par-{i}-{Guid.NewGuid():N}", ["ms"] = 700 }, cancellationToken: Ct).AsTask());
        var results = await Task.WhenAll(calls);
        Assert.All(results, r => Assert.NotEqual(true, r.IsError));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}"); // sequential would be >= 4.2 s
    }
}
