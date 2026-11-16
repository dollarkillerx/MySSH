using System.Diagnostics;
using System.Net.Sockets;
using ModelContextProtocol.Protocol;

namespace MySSH.Mcp.Tests;

/// <summary>End-to-end through the real myssh-mcp bridge process (JIT build copied next to the tests).</summary>
public sealed class BridgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireUnix() => Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix domain socket test");

    private static async Task<(int ExitCode, string Stderr)> RunToExit(Process p)
    {
        try
        {
            var stderr = p.StandardError.ReadToEndAsync(Ct);
            await p.WaitForExitAsync(Ct).WaitAsync(Wait.Timeout, Ct);
            return (p.ExitCode, await stderr);
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.Dispose();
        }
    }

    [Theory]
    [InlineData(TestHost.Latest)]
    [InlineData(TestHost.Legacy)]
    public async Task Stdio_client_through_bridge_lists_and_calls_tools(string protocol)
    {
        RequireUnix();
        await using var t = await TestHost.StartAsync(http: false);
        var client = await t.BridgeClientAsync(protocol, name: "claude-desktop");
        Assert.Equal(protocol, client.NegotiatedProtocolVersion);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        Assert.Equal(FakeCatalog.Instance.Tools.Select(d => d.Name), tools.Select(x => x.Name));
        Assert.True(tools.Single(x => x.Name == "list_hosts").ProtocolTool.Annotations!.ReadOnlyHint);

        var r = await client.CallToolAsync("run_command", new Dictionary<string, object?> { ["host"] = "web-01", ["command"] = "uptime" }, cancellationToken: Ct);
        Assert.Equal("[fake] web-01$ uptime by mcp:claude-desktop", Assert.IsType<TextContentBlock>(Assert.Single(r.Content)).Text);
        var err = await client.CallToolAsync("run_command", new Dictionary<string, object?> { ["host"] = "nope", ["command"] = "x" }, cancellationToken: Ct);
        Assert.True(err.IsError);

        var local = Assert.Single(t.Host.Clients);
        Assert.Equal(McpClientTransport.Local, local.Transport);
        Assert.Equal("claude-desktop", local.Name);
    }

    [Fact]
    public async Task Cancellation_through_bridge_reaches_the_invoker()
    {
        RequireUnix();
        await using var t = await TestHost.StartAsync(http: false);
        var client = await t.BridgeClientAsync(TestHost.Legacy);
        var marker = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        var call = client.CallToolAsync("slow", new Dictionary<string, object?> { ["marker"] = marker }, cancellationToken: cts.Token).AsTask();
        await t.Invoker.StartedFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await t.Invoker.CancelledFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
    }

    [Fact]
    public async Task Killing_the_bridge_cancels_in_flight_calls()
    {
        RequireUnix();
        await using var t = await TestHost.StartAsync(http: false);
        var client = await t.BridgeClientAsync();
        var marker = Guid.NewGuid().ToString("N");
        var call = client.CallToolAsync("slow", new Dictionary<string, object?> { ["marker"] = marker }, cancellationToken: Ct).AsTask();
        await t.Invoker.StartedFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
        await client.DisposeAsync(); // closes stdin, then kills the bridge after ShutdownTimeout
        await t.Invoker.CancelledFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
        await Assert.ThrowsAnyAsync<Exception>(() => call);
        await Wait.Until(() => t.Host.Clients.Count == 0);
    }

    [Fact]
    public async Task Bridge_pipes_bytes_and_exits_0_on_stdin_eof()
    {
        RequireUnix();
        await using var t = await TestHost.StartAsync(http: false);
        var p = Bridge.Start("--socket", t.SocketPath);
        await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"raw-bridge","version":"1"}}}""");
        await p.StandardInput.FlushAsync(Ct);
        var line = await p.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(Wait.Timeout, Ct);
        Assert.Contains("\"protocolVersion\":\"2025-11-25\"", line, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"myssh\"", line, StringComparison.Ordinal);
        p.StandardInput.Close();
        var (code, _) = await RunToExit(p);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Bridge_exits_when_MySSH_disconnects_it()
    {
        RequireUnix();
        await using var t = await TestHost.StartAsync(http: false);
        var p = Bridge.Start("--socket", t.SocketPath);
        await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"raw-bridge","version":"1"}}}""");
        await p.StandardInput.FlushAsync(Ct);
        await p.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(Wait.Timeout, Ct);
        await Wait.Until(() => t.Host.Clients.Count == 1);
        Assert.True(await t.Host.DisconnectAsync(t.Host.Clients[0].Id));
        var (code, _) = await RunToExit(p);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Bridge_exits_3_with_a_clear_message_when_the_socket_is_missing()
    {
        RequireUnix();
        var dir = TestHost.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "nope.sock");
            var (code, stderr) = await RunToExit(Bridge.Start("--socket", path));
            Assert.Equal(3, code);
            Assert.Contains("MySSH is not running", stderr, StringComparison.Ordinal);
            Assert.Contains(path, stderr, StringComparison.Ordinal);
            Assert.Contains("enable the MCP server in Settings", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Bridge_exits_3_on_a_stale_socket()
    {
        RequireUnix();
        var dir = TestHost.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "stale.sock");
            TestHost.CreateStaleSocket(path);
            var (code, stderr) = await RunToExit(Bridge.Start("--socket", path));
            Assert.Equal(3, code);
            Assert.Contains("stale socket", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Bridge_exits_1_on_bad_arguments()
    {
        var (code, stderr) = await RunToExit(Bridge.Start("--bogus"));
        Assert.Equal(1, code);
        Assert.Contains("usage: myssh-mcp", stderr, StringComparison.Ordinal);
    }
}
