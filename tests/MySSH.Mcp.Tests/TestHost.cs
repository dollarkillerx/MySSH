using System.Diagnostics;
using System.Net.Sockets;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MySSH.Mcp.Tests;

/// <summary>A real in-process <see cref="McpServerHost"/> on a random port and a short temp socket path, plus client helpers.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    public const string Latest = "2026-07-28";
    public const string Legacy = "2025-11-25";

    private readonly List<IAsyncDisposable> _clients = [];

    private TestHost(McpServerHost host, FakeInvoker invoker, string dir)
    {
        Host = host;
        Invoker = invoker;
        Directory = dir;
    }

    public McpServerHost Host { get; }
    public FakeInvoker Invoker { get; }
    public string Directory { get; }
    public string Token => Host.Token;
    public Uri Endpoint => Host.HttpEndpoint!;
    public string SocketPath => Host.LocalSocketPath!;

    public static bool UnixSockets => !OperatingSystem.IsWindows();

    /// <summary>Short (sun_path ≤ 103 bytes) private temp directory; removed on dispose.</summary>
    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    public static string SocketAddress(string dir) =>
        OperatingSystem.IsWindows() ? @"\\.\pipe\myssh-mcp-test-" + Path.GetFileName(dir) : Path.Combine(dir, "s", "mcp.sock");

    public static async Task<TestHost> StartAsync(bool http = true, bool local = true, string? dir = null, FakeInvoker? invoker = null)
    {
        dir ??= NewTempDir();
        invoker ??= new FakeInvoker();
        var host = await McpServerHost.StartAsync(new McpHostOptions
        {
            EnableHttp = http,
            Port = 0,
            Token = McpToken.Generate(),
            EnableLocalSocket = local,
            LocalSocketPath = SocketAddress(dir),
        }, FakeCatalog.Instance, invoker, loggerFactory: null, TestContext.Current.CancellationToken);
        return new TestHost(host, invoker, dir);
    }

    /// <summary>
    /// A socket file nobody listens on, as left behind by a crashed MySSH. (.NET unlinks the path of a bound socket on
    /// dispose, so bind under a temporary name and move the inode into place first.)
    /// </summary>
    public static void CreateStaleSocket(string path)
    {
        var tmp = path + ".tmp";
        using (var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            s.Bind(new UnixDomainSocketEndPoint(tmp));
            File.Move(tmp, path);
        }
        if (!File.Exists(path)) throw new InvalidOperationException("could not create a stale socket");
    }

    public static McpClientOptions ClientOptions(string? protocol, string name = "xunit-client") => new()
    {
        ClientInfo = new Implementation { Name = name, Version = "1.2.3" },
        ProtocolVersion = protocol,
    };

    public async Task<McpClient> HttpClientAsync(string? protocol = null, string? token = null, string name = "xunit-client")
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "test-http",
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token ?? Token}" },
        });
        var client = await McpClient.CreateAsync(transport, ClientOptions(protocol, name), cancellationToken: TestContext.Current.CancellationToken);
        _clients.Add(client);
        return client;
    }

    /// <summary>MCP session straight over the Unix domain socket (no bridge).</summary>
    public async Task<(McpClient Client, Socket Socket)> LocalClientAsync(string? protocol = null, string name = "xunit-local")
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), TestContext.Current.CancellationToken);
        var stream = new NetworkStream(socket, ownsSocket: true);
        var client = await McpClient.CreateAsync(new StreamClientTransport(stream, stream), ClientOptions(protocol, name),
            cancellationToken: TestContext.Current.CancellationToken);
        _clients.Add(client);
        return (client, socket);
    }

    /// <summary>MCP session through the real myssh-mcp bridge process (stdio).</summary>
    public async Task<McpClient> BridgeClientAsync(string? protocol = null, string name = "xunit-bridge")
    {
        var (command, prefix) = Bridge.Command();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = command,
            Arguments = [.. prefix, "--socket", SocketPath],
            Name = "test-bridge",
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        var client = await McpClient.CreateAsync(transport, ClientOptions(protocol, name), cancellationToken: TestContext.Current.CancellationToken);
        _clients.Add(client);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients)
        {
            try { await c.DisposeAsync(); }
            catch (Exception) { }
        }
        await Host.DisposeAsync();
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { }
    }
}

internal static class Bridge
{
    /// <summary>The bridge copied next to the tests (see the csproj): apphost when present, else `dotnet myssh-mcp.dll`.</summary>
    public static (string Command, string[] Prefix) Command()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "bridge");
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "myssh-mcp.exe" : "myssh-mcp");
        if (File.Exists(apphost)) return (apphost, []);
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } p ? p : "dotnet";
        return (dotnet, [Path.Combine(dir, "myssh-mcp.dll")]);
    }

    public static Process Start(params string[] args) => Start(new Dictionary<string, string>(), args);

    public static Process Start(IReadOnlyDictionary<string, string> env, params string[] args)
    {
        var (command, prefix) = Command();
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in prefix.Concat(args)) psi.ArgumentList.Add(a);
        psi.Environment.Remove("MYSSH_MCP_SOCKET");
        psi.Environment.Remove("MYSSH_APP");
        foreach (var (k, v) in env) psi.Environment[k] = v;
        return Process.Start(psi)!;
    }
}
