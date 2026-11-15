// myssh-mcp: stdio <-> MySSH local socket bridge (for Claude Desktop & other stdio-only MCP clients).
// Pure byte pipe: no JSON parsing, no MCP knowledge. The GUI process owns the MCP server.
using System.IO.Pipes;
using System.Net.Sockets;

const int ExitOk = 0, ExitUsage = 2, ExitNoServer = 3;

string? socketPath = Environment.GetEnvironmentVariable("MYSSH_MCP_SOCKET");
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--socket" && i + 1 < args.Length) socketPath = args[++i];
    else if (args[i].StartsWith("--socket=")) socketPath = args[i]["--socket=".Length..];
    else { Console.Error.WriteLine($"myssh-mcp: unknown argument '{args[i]}'\nusage: myssh-mcp [--socket PATH]"); return ExitUsage; }
}
// Production default would be ~/Library/Application Support/MySSH/mcp.sock ($XDG_RUNTIME_DIR/myssh/mcp.sock on Linux).
socketPath ??= Path.Combine(Environment.CurrentDirectory, ".work", "myssh.sock");

Stream server;
Socket? socket = null;
try
{
    if (OperatingSystem.IsWindows())
    {
        // Named pipe; CurrentUserOnly also verifies the pipe server runs as the same user (anti-squatting).
        var pipe = new NamedPipeClientStream(".", $"myssh-mcp-{Environment.UserName}", PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2000);
        server = pipe;
    }
    else
    {
        if (!File.Exists(socketPath)) return Fail($"MySSH is not running (socket not found: {socketPath}).");
        socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        server = new NetworkStream(socket, ownsSocket: true);
    }
}
catch (Exception ex) when (ex is SocketException or TimeoutException or IOException)
{
    return Fail($"MySSH is not accepting MCP connections ({ex.Message}). Socket/pipe may be stale.");
}

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

var up = PumpAsync(stdin, server);     // client -> GUI
var down = PumpAsync(server, stdout);  // GUI -> client

var first = await Task.WhenAny(up, down);
if (first == up)
{
    // stdin EOF: client is done. Half-close so the server sees EOF, then drain pending responses briefly.
    try { socket?.Shutdown(SocketShutdown.Send); } catch { }
    await Task.WhenAny(down, Task.Delay(2000));
}
// If the server side closed first, we just exit; the blocked stdin read dies with the process.
server.Dispose();
return ExitOk;

static async Task PumpAsync(Stream from, Stream to)
{
    var buf = new byte[64 * 1024];
    try
    {
        int n;
        while ((n = await from.ReadAsync(buf)) > 0)
        {
            await to.WriteAsync(buf.AsMemory(0, n));
            await to.FlushAsync(); // newline-delimited JSON-RPC: never sit on a partial buffer
        }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
}

static int Fail(string message)
{
    Console.Error.WriteLine("myssh-mcp: " + message);
    Console.Error.WriteLine("myssh-mcp: TODO(production): launch the MySSH GUI in tray mode (`open -b <bundle-id> --args --tray` / Process.Start) and retry for ~10s.");
    return ExitNoServer;
}
