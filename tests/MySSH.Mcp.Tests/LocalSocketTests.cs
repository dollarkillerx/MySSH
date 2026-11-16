using System.Net.Sockets;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MySSH.Mcp.Tests;

public sealed class LocalSocketTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireUnix() => Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix domain socket test");

    [Theory]
    [InlineData(TestHost.Latest)]
    [InlineData(TestHost.Legacy)]
    public async Task Local_session_lists_and_calls_tools_with_client_label(string protocol)
    {
        RequireUnix();
        await using var t = await TestHost.StartAsync(http: false);
        Assert.Null(t.Host.HttpEndpoint);
        Assert.Null(t.Host.Port);
        var (client, _) = await t.LocalClientAsync(protocol, name: "claude-desktop");
        Assert.Equal(protocol, client.NegotiatedProtocolVersion);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        Assert.Equal(FakeCatalog.Instance.Tools.Count, tools.Count);
        Assert.True(tools.Single(x => x.Name == "run_command").ProtocolTool.Annotations!.DestructiveHint);

        var r = await client.CallToolAsync("run_command", new Dictionary<string, object?> { ["host"] = "web-01", ["command"] = "id" }, cancellationToken: Ct);
        Assert.Equal("[fake] web-01$ id by mcp:claude-desktop", Assert.IsType<TextContentBlock>(Assert.Single(r.Content)).Text);
        var actor = Assert.Single(t.Invoker.Calls).Actor;
        Assert.Equal("claude-desktop (MCP)", actor.DisplayName);

        var res = await client.ReadResourceAsync("myssh://hosts", cancellationToken: Ct);
        Assert.Single(res.Contents);
        var prompts = await client.ListPromptsAsync(cancellationToken: Ct);
        Assert.Equal(2, prompts.Count);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Socket_is_0600_in_a_0700_directory_and_removed_on_stop()
    {
        RequireUnix();
        var t = await TestHost.StartAsync(http: false);
        var path = t.SocketPath;
        try
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }
        finally
        {
            await t.Host.StopAsync();
            await t.Host.StopAsync(); // idempotent
        }
        Assert.False(File.Exists(path));
        await t.DisposeAsync();
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Existing_directory_is_tightened_to_0700()
    {
        RequireUnix();
        var dir = TestHost.NewTempDir();
        var sockDir = Path.Combine(dir, "s");
        Directory.CreateDirectory(sockDir);
        File.SetUnixFileMode(sockDir, (UnixFileMode)0b111_101_101); // 0755
        await using var t = await TestHost.StartAsync(http: false, dir: dir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(sockDir));
    }

    [Fact]
    public async Task Stale_socket_is_removed_and_rebound()
    {
        RequireUnix();
        var dir = TestHost.NewTempDir();
        var path = TestHost.SocketAddress(dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TestHost.CreateStaleSocket(path);
        Assert.True(File.Exists(path));

        await using var t = await TestHost.StartAsync(http: false, dir: dir);
        var (client, _) = await t.LocalClientAsync();
        Assert.NotEmpty(await client.ListToolsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Second_instance_gets_McpInstanceRunningException_and_leaves_the_first_alone()
    {
        RequireUnix();
        await using var first = await TestHost.StartAsync(http: false);
        var ex = await Assert.ThrowsAsync<McpInstanceRunningException>(() => McpServerHost.StartAsync(
            new McpHostOptions { EnableHttp = true, EnableLocalSocket = true, LocalSocketPath = first.SocketPath },
            FakeCatalog.Instance, new FakeInvoker(), ct: Ct));
        Assert.Equal(first.SocketPath, ex.Address);

        Assert.True(File.Exists(first.SocketPath));
        var (client, _) = await first.LocalClientAsync();
        Assert.NotEmpty(await client.ListToolsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Too_long_socket_path_fails_with_a_clear_error()
    {
        RequireUnix();
        var dir = TestHost.NewTempDir();
        try
        {
            var path = Path.Combine(dir, new string('d', 120), "mcp.sock");
            var ex = await Assert.ThrowsAsync<PathTooLongException>(() => McpServerHost.StartAsync(
                new McpHostOptions { EnableHttp = false, LocalSocketPath = path }, FakeCatalog.Instance, new FakeInvoker(), ct: Ct));
            Assert.Contains("Unix domain sockets allow at most", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Client_cancellation_reaches_the_invoker()
    {
        RequireUnix();
        await using var t = await TestHost.StartAsync(http: false);
        var (client, _) = await t.LocalClientAsync(TestHost.Legacy);
        var marker = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        var call = client.CallToolAsync("slow", new Dictionary<string, object?> { ["marker"] = marker }, cancellationToken: cts.Token).AsTask();
        await t.Invoker.StartedFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
        await cts.CancelAsync(); // notifications/cancelled
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await t.Invoker.CancelledFor(marker).Task.WaitAsync(Wait.Timeout, Ct);

        // the session survives a cancelled call
        Assert.NotEqual(true, (await client.CallToolAsync("list_hosts", cancellationToken: Ct)).IsError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eof_cancels_in_flight_calls(bool halfClose)
    {
        RequireUnix();
        await using var t = await TestHost.StartAsync(http: false);
        var (client, socket) = await t.LocalClientAsync();
        var marker = Guid.NewGuid().ToString("N");
        var call = client.CallToolAsync("slow", new Dictionary<string, object?> { ["marker"] = marker }, cancellationToken: Ct).AsTask();
        await t.Invoker.StartedFor(marker).Task.WaitAsync(Wait.Timeout, Ct);

        if (halfClose) socket.Shutdown(SocketShutdown.Send); // what the bridge does on stdin EOF
        else socket.Close();

        await t.Invoker.CancelledFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
        Assert.False(t.Invoker.Completed.ContainsKey(marker));
        await Assert.ThrowsAnyAsync<Exception>(() => call);
        await Wait.Until(() => t.Host.Clients.Count == 0);
    }

    [Fact]
    public async Task Clients_list_local_sessions_and_DisconnectAsync_closes_them()
    {
        RequireUnix();
        await using var t = await TestHost.StartAsync(http: false);
        var changed = 0;
        t.Host.ClientsChanged += () => Interlocked.Increment(ref changed);

        var (a, _) = await t.LocalClientAsync(TestHost.Legacy, name: "client-a");
        var (b, _) = await t.LocalClientAsync(TestHost.Latest, name: "client-b");
        await a.CallToolAsync("list_hosts", cancellationToken: Ct);
        await b.ListToolsAsync(cancellationToken: Ct);

        var clients = t.Host.Clients;
        Assert.Equal(2, clients.Count);
        Assert.All(clients, c => Assert.Equal(McpClientTransport.Local, c.Transport));
        Assert.All(clients, c => Assert.True(c.IsConnected));
        var ca = clients.Single(c => c.Name == "client-a");
        var cb = clients.Single(c => c.Name == "client-b");
        Assert.StartsWith("local:", ca.Id, StringComparison.Ordinal);
        Assert.Equal(1, ca.CallCount);
        Assert.Equal(0, cb.CallCount);
        Assert.True(changed > 0);

        // disconnect a while a call is waiting (e.g. on an approval prompt): the call is cancelled
        var marker = Guid.NewGuid().ToString("N");
        var call = a.CallToolAsync("slow", new Dictionary<string, object?> { ["marker"] = marker }, cancellationToken: Ct).AsTask();
        await t.Invoker.StartedFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
        Assert.Equal(1, t.Host.Clients.Single(c => c.Id == ca.Id).InFlightCalls);

        Assert.True(await t.Host.DisconnectAsync(ca.Id));
        await t.Invoker.CancelledFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
        await Assert.ThrowsAnyAsync<Exception>(() => call);
        Assert.Equal(cb.Id, Assert.Single(t.Host.Clients).Id);

        // b is unaffected
        Assert.NotEmpty(await b.ListToolsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Concurrent_http_and_local_clients()
    {
        RequireUnix();
        await using var t = await TestHost.StartAsync();
        var http = await t.HttpClientAsync();
        var (local1, _) = await t.LocalClientAsync();
        var (local2, _) = await t.LocalClientAsync(TestHost.Legacy);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await Task.WhenAll(new[] { http, local1, local2, http, local1, local2 }.Select((c, i) =>
            c.CallToolAsync("slow", new Dictionary<string, object?> { ["marker"] = $"c{i}-{Guid.NewGuid():N}", ["ms"] = 700 }, cancellationToken: Ct).AsTask()));
        Assert.All(results, r => Assert.NotEqual(true, r.IsError));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Assert.Equal(3, t.Host.Clients.Count); // one http entry (same name) + two local sessions
    }

    [Fact]
    public async Task StopAsync_cancels_in_flight_local_calls_and_closes_sessions()
    {
        RequireUnix();
        var t = await TestHost.StartAsync(http: false);
        var (client, _) = await t.LocalClientAsync();
        var marker = Guid.NewGuid().ToString("N");
        var call = client.CallToolAsync("slow", new Dictionary<string, object?> { ["marker"] = marker }, cancellationToken: Ct).AsTask();
        await t.Invoker.StartedFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await t.Host.StopAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"stop took {sw.Elapsed}");
        await t.Invoker.CancelledFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
        await Assert.ThrowsAnyAsync<Exception>(() => call);
        Assert.Empty(t.Host.Clients);
        await t.DisposeAsync();
    }

    [Fact]
    public async Task Windows_named_pipe_session()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "named pipe test");
        await using var t = await TestHost.StartAsync(http: false);
        var pipe = new System.IO.Pipes.NamedPipeClientStream(".", t.SocketPath[@"\\.\pipe\".Length..], System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, Ct);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), TestHost.ClientOptions(null), cancellationToken: Ct);
        Assert.NotEmpty(await client.ListToolsAsync(cancellationToken: Ct));
        await Assert.ThrowsAsync<McpInstanceRunningException>(() => McpServerHost.StartAsync(
            new McpHostOptions { EnableHttp = false, LocalSocketPath = t.SocketPath }, FakeCatalog.Instance, new FakeInvoker(), ct: Ct));
    }
}
