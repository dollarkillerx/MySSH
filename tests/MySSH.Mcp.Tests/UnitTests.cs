using System.Text.Json;

namespace MySSH.Mcp.Tests;

public sealed class UnitTests
{
    [Fact]
    public void Token_is_32_random_bytes_base64url()
    {
        var a = McpToken.Generate();
        var b = McpToken.Generate();
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
        var padded = a.Replace('-', '+').Replace('_', '/') + "=";
        Assert.Equal(32, Convert.FromBase64String(padded).Length);
    }

    [Fact]
    public void ClaudeCodeCommand_matches_the_documented_form()
    {
        Assert.Equal(
            "claude mcp add --transport http myssh http://127.0.0.1:41234/mcp --header \"Authorization: Bearer tok_123\"",
            McpClientSetup.ClaudeCodeCommand(new Uri("http://127.0.0.1:41234/mcp"), "tok_123"));
    }

    [Fact]
    public void ClaudeDesktopConfig_uses_the_stdio_bridge()
    {
        var json = McpClientSetup.ClaudeDesktopConfigJson("/Applications/MySSH.app/Contents/MacOS/myssh-mcp");
        var server = JsonDocument.Parse(json).RootElement.GetProperty("mcpServers").GetProperty("myssh");
        Assert.Equal("/Applications/MySSH.app/Contents/MacOS/myssh-mcp", server.GetProperty("command").GetString());
        Assert.False(server.TryGetProperty("args", out _));
        Assert.Contains("\n", json, StringComparison.Ordinal); // indented for humans

        var withSocket = JsonDocument.Parse(McpClientSetup.ClaudeDesktopConfigJson(@"C:\Program Files\MySSH\myssh-mcp.exe", "/tmp/x.sock"))
            .RootElement.GetProperty("mcpServers").GetProperty("myssh");
        Assert.Equal(@"C:\Program Files\MySSH\myssh-mcp.exe", withSocket.GetProperty("command").GetString());
        Assert.Equal(["--socket", "/tmp/x.sock"], withSocket.GetProperty("args").EnumerateArray().Select(e => e.GetString()));

        Assert.Contains("ユーザー", McpClientSetup.ClaudeDesktopConfigJson("/Users/ユーザー/myssh-mcp"), StringComparison.Ordinal); // not \u-escaped
    }

    [Fact]
    public void HttpConfig_carries_url_and_bearer_header()
    {
        var server = JsonDocument.Parse(McpClientSetup.HttpConfigJson(new Uri("http://127.0.0.1:5/mcp"), "t0k"))
            .RootElement.GetProperty("mcpServers").GetProperty("myssh");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:5/mcp", server.GetProperty("url").GetString());
        Assert.Equal("Bearer t0k", server.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Fact]
    public void DefaultBridgePath_is_next_to_the_app()
    {
        var p = McpClientSetup.DefaultBridgePath();
        Assert.Equal(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(p));
        Assert.StartsWith("myssh-mcp", Path.GetFileName(p), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost", true)]
    [InlineData("http://LOCALHOST:3000", true)]
    [InlineData("https://127.0.0.1:1", true)]
    [InlineData("http://[::1]:5173", true)]
    [InlineData("null", false)]
    [InlineData("http://10.0.0.1", false)]
    [InlineData("http://localhost.evil.example", false)]
    [InlineData("chrome-extension://abc", false)]
    [InlineData("http://user:pw@localhost", false)]
    public void Origin_check(string origin, bool allowed) => Assert.Equal(allowed, HttpSecurity.IsLoopbackOrigin(origin));

    [Theory]
    [InlineData("claude-code", "claude-code")]
    [InlineData("  Claude Desktop  ", "Claude Desktop")]
    [InlineData("a\nb", "a_b")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Client_names_are_sanitized(string? input, string? expected) => Assert.Equal(expected, McpActors.SanitizeName(input));

    [Fact]
    public void Actor_labels()
    {
        Assert.Equal("mcp:claude-code", McpActors.Create(McpClientTransport.Http, "claude-code").Label);
        Assert.Equal("claude-code (MCP)", McpActors.Create(McpClientTransport.Http, "claude-code").DisplayName);
        Assert.Equal("mcp:http", McpActors.Create(McpClientTransport.Http, null).Label);
        Assert.Equal("mcp:local", McpActors.Create(McpClientTransport.Local, null).Label);
        Assert.Equal(MySSH.Core.ConnectionOrigin.Mcp, McpActors.Create(McpClientTransport.Local, null).Origin);
    }

    [Theory]
    [InlineData("myssh://sessions/abc/screen", "abc")]
    [InlineData("myssh://sessions/a%20b/screen", "a b")]
    [InlineData("myssh://sessions//screen", null)]
    [InlineData("myssh://sessions/a/b/screen", null)]
    [InlineData("myssh://hosts", null)]
    public void Screen_uri_parsing(string uri, string? id)
    {
        Assert.Equal(id is not null, McpToolAdapter.TryParseScreenUri(uri, out var parsed));
        if (id is not null) Assert.Equal(id, parsed);
    }

    [Fact]
    public void Default_socket_path_per_platform()
    {
        var path = McpServerHost.DefaultLocalSocketPath;
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"\\.\pipe\myssh-mcp-" + Environment.UserName, path);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MySSH.Desktop", "mcp.sock"), path);
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            Assert.Equal(string.IsNullOrEmpty(xdg)
                ? $"/tmp/myssh-desktop-{LocalEndpoint.CurrentUid()}/mcp.sock"
                : Path.Combine(xdg, "myssh-desktop", "mcp.sock"), path);
        }
    }

    [Fact]
    public void Peer_credentials_identify_the_current_user()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        var dir = TestHost.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "u.sock");
            using var server = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            server.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            server.Listen(1);
            using var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            client.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            using var accepted = server.Accept();
            Assert.True(LocalEndpoint.PeerIsCurrentUser(accepted, out var peer));
            Assert.Equal(LocalEndpoint.CurrentUid(), peer);
            Assert.True(LocalEndpoint.PeerIsCurrentUser(client, out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
