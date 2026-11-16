// myssh-mcp: stdio <-> MySSH local MCP socket bridge (design §6.4), for Claude Desktop and other stdio-only MCP clients.
// A pure byte pump: no JSON parsing, no MCP knowledge. The MySSH GUI process owns the MCP server, policy and approvals.
//
// usage: myssh-mcp [--socket PATH] [--no-launch]   (or MYSSH_MCP_SOCKET=PATH; on Windows PATH is a pipe name or \\.\pipe\name)
// exit:  0 stdin EOF / server closed, 3 MySSH is not running (no socket / nobody listening), 1 any other error.
//
// When MySSH is not running and the default socket is used, the bridge starts the MySSH executable that sits next to it
// (or MYSSH_APP) with --tray and waits up to 60 s for the MCP socket (the vault may need the master password first).
// MySSH itself is single-instance, so a launch never opens a second copy.
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using MySSH.Mcp;

const int ExitOk = 0, ExitError = 1, ExitNotRunning = 3;
const string Usage = "usage: myssh-mcp [--socket PATH] [--no-launch]";

string? address = Environment.GetEnvironmentVariable("MYSSH_MCP_SOCKET");
var launch = true;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--socket" && i + 1 < args.Length) address = args[++i];
    else if (args[i].StartsWith("--socket=", StringComparison.Ordinal)) address = args[i]["--socket=".Length..];
    else if (args[i] == "--no-launch") launch = false;
    else if (args[i] is "-h" or "--help")
    {
        Console.Error.WriteLine(Usage + "\nBridges stdio to the MCP server of the running MySSH app (starting it in the tray if needed).");
        return ExitOk;
    }
    else
    {
        Console.Error.WriteLine($"myssh-mcp: unknown argument '{args[i]}'\n{Usage}");
        return ExitError;
    }
}

try
{
    var custom = !string.IsNullOrEmpty(address);
    address = custom ? address! : LocalEndpoint.DefaultAddress();
    var (server, socket, notRunning) = await ConnectAsync(address);
    // Auto-launch for the default endpoint, or when MYSSH_APP names the app explicitly (a custom socket alone belongs to
    // some other setup that we must not start a GUI for).
    var appExplicit = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MYSSH_APP"));
    if (server is null && launch && (!custom || appExplicit) && LaunchApp() is { } app)
    {
        Console.Error.WriteLine($"myssh-mcp: MySSH is not running; started {app} --tray, waiting for its MCP server...");
        var until = DateTime.UtcNow.AddSeconds(60);
        while (server is null && DateTime.UtcNow < until)
        {
            await Task.Delay(300);
            (server, socket, notRunning) = await ConnectAsync(address);
        }
    }
    if (server is null)
    {
        NotRunning(notRunning ?? address);
        return ExitNotRunning;
    }
    await using (server)
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var up = PumpAsync(stdin, server);    // client -> MySSH
        var down = PumpAsync(server, stdout); // MySSH -> client
        if (await Task.WhenAny(up, down) == up)
        {
            // stdin EOF: the client is done. Half-close so the server sees EOF (it cancels the session), then let
            // already-written responses drain for a moment.
            try { socket?.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
            await Task.WhenAny(down, Task.Delay(2000));
        }
        // Server closed first (MySSH quit or the user disconnected us): exit; the blocked stdin read dies with us.
    }
    return ExitOk;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"myssh-mcp: {ex.Message}");
    return ExitError;
}

// Starts MySSH (MYSSH_APP or the MySSH executable next to this bridge) in tray mode. Returns its path, or null.
static string? LaunchApp()
{
    var app = Environment.GetEnvironmentVariable("MYSSH_APP");
    if (string.IsNullOrEmpty(app))
        app = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "MySSH.exe" : "MySSH");
    if (!File.Exists(app)) return null;
    try
    {
        // stdio is redirected: this process's stdout is the MCP channel, the GUI must never write into it.
        var psi = new ProcessStartInfo(app)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(app)!,
        };
        psi.ArgumentList.Add("--tray");
        var p = Process.Start(psi);
        if (p is null) return null;
        p.StandardInput.Close();
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return app;
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
    {
        Console.Error.WriteLine($"myssh-mcp: could not start {app}: {ex.Message}");
        return null;
    }
}

static async Task<(Stream? Stream, Socket? Socket, string? NotRunning)> ConnectAsync(string address)
{
    if (OperatingSystem.IsWindows())
    {
        // CurrentUserOnly also verifies the pipe server runs as the same user (anti-squatting).
        var pipe = new NamedPipeClientStream(".", LocalEndpoint.PipeName(address), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(2000);
            return (pipe, null, null);
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync();
            return (null, null, $"nobody is listening on {address}");
        }
    }

    LocalEndpoint.ValidateSocketPath(address);
    if (!File.Exists(address))
        return (null, null, $"socket not found: {address}");
    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    try
    {
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(address));
    }
    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable)
    {
        socket.Dispose();
        return (null, null, $"stale socket, nobody is listening: {address}");
    }
    if (!LocalEndpoint.PeerIsCurrentUser(socket, out var peer))
    {
        socket.Dispose();
        throw new IOException($"refusing to talk to {address}: it is owned by uid {peer?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}, not by the current user");
    }
    return (new NetworkStream(socket, ownsSocket: true), socket, null);
}

static void NotRunning(string detail)
{
    Console.Error.WriteLine($"myssh-mcp: MySSH is not running ({detail}).");
    Console.Error.WriteLine("myssh-mcp: start MySSH and enable the MCP server in Settings.");
}

static async Task PumpAsync(Stream from, Stream to)
{
    var buffer = new byte[64 * 1024];
    try
    {
        int n;
        while ((n = await from.ReadAsync(buffer)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, n));
            await to.FlushAsync(); // newline-delimited JSON-RPC: never sit on a partial buffer
        }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
}
