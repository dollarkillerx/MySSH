// McpProbe: end-to-end checks against MySSH.Spike.McpHost using the official MCP C# SDK client.
// usage: McpProbe --work-dir DIR --bridge PATH_TO_myssh-mcp --phase allow|deny|timeout [--timeout-s N]
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

string workDir = Path.GetFullPath(".work"), bridge = "", phase = "allow";
double uiTimeout = 5;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--work-dir": workDir = Path.GetFullPath(args[++i]); break;
        case "--bridge": bridge = Path.GetFullPath(args[++i]); break;
        case "--phase": phase = args[++i]; break;
        case "--timeout-s": uiTimeout = double.Parse(args[++i]); break;
    }
}

var endpointFile = Path.Combine(workDir, "endpoint.json");
var eventsFile = Path.Combine(workDir, "events.log");
var ep = JsonDocument.Parse(File.ReadAllText(endpointFile)).RootElement;
string url = ep.GetProperty("url").GetString()!, token = ep.GetProperty("token").GetString()!, sock = ep.GetProperty("socket").GetString()!;
Console.WriteLine($"# phase={phase} url={url} socket={sock} serverMode={ep.GetProperty("httpSessionMode")} approval={ep.GetProperty("approval")}");

var results = new List<(string Name, bool Pass, string Evidence)>();
void Report(string name, bool pass, string evidence)
{
    results.Add((name, pass, evidence));
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}  -- {evidence}");
}
async Task Check(string name, Func<Task<(bool, string)>> body)
{
    try { var (p, e) = await body(); Report(name, p, e); }
    catch (Exception ex) { Report(name, false, $"exception {ex.GetType().Name}: {ex.Message}"); }
}

var clientOptions = (string? proto) => new McpClientOptions
{
    ClientInfo = new Implementation { Name = "McpProbe", Version = "1.0" },
    ProtocolVersion = proto,
};

async Task<McpClient> HttpClientAsync(string? proto = null, string? tok = null, CancellationToken ct = default)
{
    var transport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri(url),
        TransportMode = HttpTransportMode.StreamableHttp,
        Name = "probe-http",
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {tok ?? token}" },
    });
    return await McpClient.CreateAsync(transport, clientOptions(proto), cancellationToken: ct);
}

async Task<McpClient> StdioClientAsync(string? proto = null)
{
    var transport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = bridge,
        Arguments = ["--socket", sock],
        Name = "probe-stdio-bridge",
        ShutdownTimeout = TimeSpan.FromSeconds(2),
    });
    return await McpClient.CreateAsync(transport, clientOptions(proto));
}

static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
static Dictionary<string, object?> Cmd(string host, string command) => new() { ["host"] = host, ["command"] = command };

// Polls events.log for a line containing all needles (server-side evidence).
async Task<string?> WaitEvent(TimeSpan timeout, params string[] needles)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < timeout)
    {
        var line = File.ReadLines(eventsFile).LastOrDefault(l => needles.All(l.Contains));
        if (line is not null) return line;
        await Task.Delay(100);
    }
    return null;
}

// ---- common: list_tools / list_hosts / run_command via a given client ----
async Task BasicSuite(string label, Func<Task<McpClient>> connect, string expectRun)
{
    McpClient? c = null;
    await Check($"{label}: connect", async () =>
    {
        var sw = Stopwatch.StartNew();
        c = await connect();
        return (true, $"negotiated={c.NegotiatedProtocolVersion} sessionId={c.SessionId ?? "<none>"} server={c.ServerInfo.Name} in {sw.ElapsedMilliseconds}ms");
    });
    if (c is null) return;
    await using var _ = c;

    await Check($"{label}: tools/list + annotations", async () =>
    {
        var tools = await c.ListToolsAsync();
        var lh = tools.Single(t => t.Name == "list_hosts").ProtocolTool;
        var rc = tools.Single(t => t.Name == "run_command").ProtocolTool;
        var ok = lh.Annotations?.ReadOnlyHint == true && rc.Annotations?.DestructiveHint == true && rc.Annotations?.ReadOnlyHint == false;
        return (ok, $"[{string.Join(",", tools.Select(t => t.Name))}] list_hosts.readOnlyHint={lh.Annotations?.ReadOnlyHint} run_command.destructiveHint={rc.Annotations?.DestructiveHint} run_command.schema={rc.InputSchema.GetRawText()}");
    });

    await Check($"{label}: call list_hosts", async () =>
    {
        var r = await c.CallToolAsync("list_hosts", new Dictionary<string, object?> { ["query"] = "01" });
        var t = Text(r);
        return (r.IsError != true && t.Contains("web-01") && t.Contains("db-01") && !t.Contains("lab-pi"), t.Replace("\n", " "));
    });

    await Check($"{label}: call run_command (expect {expectRun})", async () =>
    {
        var cmd = $"uptime #{label}-{Guid.NewGuid():N}"[..40];
        var sw = Stopwatch.StartNew();
        var r = await c.CallToolAsync("run_command", Cmd("web-01", cmd));
        var t = Text(r);
        var ok = expectRun switch
        {
            "allow" => r.IsError != true && t.Contains("[fake]") && sw.Elapsed >= TimeSpan.FromSeconds(4.9),
            "deny" => r.IsError == true && t.Contains("denied (Deny)"),
            _ => false,
        };
        return (ok, $"isError={r.IsError} {sw.ElapsedMilliseconds}ms text={t.Replace("\n", " | ")}");
    });
}

// ---- cancellation: start run_command, cancel/disconnect, verify server token fired ----
async Task CancelCheck(string name, Func<Task<McpClient>> connect, bool disconnect, double cancelAfterS = 1.5)
{
    await Check(name, async () =>
    {
        var c = await connect();
        var marker = $"sleep-{Guid.NewGuid():N}"[..20];
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var call = c.CallToolAsync("run_command", Cmd("web-01", marker), cancellationToken: cts.Token).AsTask();
        await WaitEvent(TimeSpan.FromSeconds(5), "run_command BEGIN", marker);
        await Task.Delay(TimeSpan.FromSeconds(cancelAfterS));
        string clientSide;
        if (disconnect)
        {
            await c.DisposeAsync(); // tear down the transport (HTTP: abort request; stdio: kill bridge process)
            try { await call; clientSide = "call completed?!"; } catch (Exception ex) { clientSide = ex.GetType().Name; }
        }
        else
        {
            cts.Cancel(); // SDK sends notifications/cancelled
            try { await call; clientSide = "call completed?!"; } catch (Exception ex) { clientSide = ex.GetType().Name; }
        }
        var clientMs = sw.ElapsedMilliseconds;
        var ev = await WaitEvent(TimeSpan.FromSeconds(4), "TOKEN CANCELLED", marker);
        var done = File.ReadLines(eventsFile).Any(l => l.Contains("run_command END") && l.Contains(marker));
        if (!disconnect) await c.DisposeAsync();
        return (ev is not null && !done,
            $"client={clientSide}@{clientMs}ms server={(ev is null ? (done ? "NOT cancelled, ran to END" : "no cancel event") : ev[..12] + " TOKEN CANCELLED")} proto={c.NegotiatedProtocolVersion}");
    });
}

async Task<HttpResponseMessage> RawPost(string? auth, string? origin, string? host = null)
{
    using var http = new HttpClient();
    var req = new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"raw","version":"1"}}}""", Encoding.UTF8, "application/json"),
    };
    req.Headers.Accept.ParseAdd("application/json");
    req.Headers.Accept.ParseAdd("text/event-stream");
    if (auth is not null) req.Headers.TryAddWithoutValidation("Authorization", auth);
    if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin);
    if (host is not null) req.Headers.Host = host;
    return await http.SendAsync(req);
}

switch (phase)
{
    case "allow":
        await BasicSuite("http(default proto)", () => HttpClientAsync(), "allow");
        await BasicSuite("http(2025-11-25)", () => HttpClientAsync("2025-11-25"), "allow");
        await BasicSuite("stdio-bridge", () => StdioClientAsync(), "allow");

        await Check("http: missing token -> 401", async () => { var r = await RawPost(null, null); return (r.StatusCode == HttpStatusCode.Unauthorized, $"{(int)r.StatusCode} WWW-Authenticate={r.Headers.WwwAuthenticate}"); });
        await Check("http: wrong token -> 401", async () => { var r = await RawPost("Bearer nope", null); return (r.StatusCode == HttpStatusCode.Unauthorized, $"{(int)r.StatusCode}"); });
        await Check("http: SDK client wrong token fails", async () =>
        {
            try { await using var c = await HttpClientAsync(tok: "nope"); return (false, "connected?!"); }
            catch (Exception ex) { return (ex.ToString().Contains("401") || ex.ToString().Contains("Unauthorized"), $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
        });
        await Check("http: bad Origin -> 403", async () => { var r = await RawPost($"Bearer {token}", "https://evil.example"); return (r.StatusCode == HttpStatusCode.Forbidden, $"{(int)r.StatusCode}"); });
        await Check("http: Origin null -> 403", async () => { var r = await RawPost($"Bearer {token}", "null"); return (r.StatusCode == HttpStatusCode.Forbidden, $"{(int)r.StatusCode}"); });
        await Check("http: loopback Origin accepted", async () => { var r = await RawPost($"Bearer {token}", "http://localhost:5173"); return (r.StatusCode == HttpStatusCode.OK, $"{(int)r.StatusCode} {r.Content.Headers.ContentType} Mcp-Session-Id={(r.Headers.TryGetValues("Mcp-Session-Id", out var v) ? v.First() : "<absent>")}"); });
        await Check("http: rebinding Host header -> 421", async () => { var r = await RawPost($"Bearer {token}", null, "attacker.example:" + new Uri(url).Port); return (r.StatusCode == HttpStatusCode.MisdirectedRequest, $"{(int)r.StatusCode}"); });

        await Check("bridge: socket missing -> clear error", async () =>
        {
            var p = Process.Start(new ProcessStartInfo(bridge, ["--socket", Path.Combine(workDir, "nope.sock")]) { RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode == 3 && err.Contains("not running"), $"exit={p.ExitCode} stderr={err.Split('\n')[0]}");
        });
        await Check("bridge: raw byte pipe + clean exit on stdin EOF", async () =>
        {
            var p = Process.Start(new ProcessStartInfo(bridge, ["--socket", sock]) { RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
            await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"raw-bridge","version":"1"}}}""");
            await p.StandardInput.FlushAsync();
            var line = await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var sw = Stopwatch.StartNew();
            p.StandardInput.Close();
            var exited = await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(4000)) is var t && p.HasExited;
            if (!exited) p.Kill();
            return (line?.Contains("\"protocolVersion\":\"2025-11-25\"") == true && exited && p.ExitCode == 0,
                $"resp={line?[..Math.Min(90, line.Length)]}... exited={exited} code={(exited ? p.ExitCode : -1)} in {sw.ElapsedMilliseconds}ms");
        });

        await CancelCheck("cancel: http(default proto) CTS", () => HttpClientAsync(), disconnect: false);
        await CancelCheck("cancel: http(2025-11-25) CTS", () => HttpClientAsync("2025-11-25"), disconnect: false);
        await CancelCheck("cancel: http(default proto) dispose client", () => HttpClientAsync(), disconnect: true);
        await CancelCheck("cancel: stdio-bridge CTS (notifications/cancelled)", () => StdioClientAsync(), disconnect: false);
        await CancelCheck("cancel: stdio-bridge dispose client (bridge killed)", () => StdioClientAsync(), disconnect: true);

        await Check("concurrency: 3x run_command in parallel (http+http+stdio)", async () =>
        {
            await using var a = await HttpClientAsync();
            await using var b = await StdioClientAsync();
            var sw = Stopwatch.StartNew();
            var rs = await Task.WhenAll(
                a.CallToolAsync("run_command", Cmd("web-01", "par-1")).AsTask(),
                a.CallToolAsync("run_command", Cmd("db-01", "par-2")).AsTask(),
                b.CallToolAsync("run_command", Cmd("lab-pi", "par-3")).AsTask());
            return (rs.All(r => r.IsError != true) && sw.Elapsed < TimeSpan.FromSeconds(8), $"all ok in {sw.ElapsedMilliseconds}ms (sequential would be >=15000)");
        });
        break;

    case "cancel": // quick cancellation-only matrix (used for the stateful/hybrid comparison run)
        await CancelCheck("cancel: http(default proto) CTS", () => HttpClientAsync(), disconnect: false);
        await CancelCheck("cancel: http(2025-11-25) CTS", () => HttpClientAsync("2025-11-25"), disconnect: false);
        await CancelCheck("cancel: http(2025-11-25) dispose client", () => HttpClientAsync("2025-11-25"), disconnect: true);
        await CancelCheck("cancel: stdio-bridge CTS (notifications/cancelled)", () => StdioClientAsync(), disconnect: false);
        break;

    case "deny":
        await BasicSuite("http(default proto)", () => HttpClientAsync(), "deny");
        await BasicSuite("stdio-bridge", () => StdioClientAsync(), "deny");
        break;

    case "timeout":
        await Check($"ui: no click -> timeout deny (~{uiTimeout}s)", async () =>
        {
            await using var c = await HttpClientAsync();
            var sw = Stopwatch.StartNew();
            var r = await c.CallToolAsync("run_command", Cmd("web-01", "rm -rf /tmp/spike-demo"));
            var t = Text(r);
            var shown = await WaitEvent(TimeSpan.FromSeconds(1), "APPROVAL WINDOW SHOWN");
            return (r.IsError == true && t.Contains("Timeout") && sw.Elapsed.TotalSeconds >= uiTimeout - 0.2 && sw.Elapsed.TotalSeconds < uiTimeout + 3 && shown is not null,
                $"isError={r.IsError} {sw.ElapsedMilliseconds}ms text={t} windowEvent={(shown is null ? "none" : "seen")}");
        });
        await Check("ui: client cancels while dialog open -> window closed, token cancelled", async () =>
        {
            await using var c = await HttpClientAsync();
            var marker = $"cancel-dlg-{Guid.NewGuid():N}"[..24];
            using var cts = new CancellationTokenSource();
            var call = c.CallToolAsync("run_command", Cmd("web-01", marker), cancellationToken: cts.Token).AsTask();
            await WaitEvent(TimeSpan.FromSeconds(5), "APPROVAL WINDOW SHOWN");
            await Task.Delay(1000);
            cts.Cancel();
            string cs; try { await call; cs = "completed?!"; } catch (Exception ex) { cs = ex.GetType().Name; }
            var ev = await WaitEvent(TimeSpan.FromSeconds(3), "run_command CANCELLED", marker);
            return (ev is not null, $"client={cs} server={(ev is null ? "no cancel event" : "CANCELLED during approval")}");
        });
        break;
}

var failed = results.Count(r => !r.Pass);
Console.WriteLine($"# phase={phase}: {results.Count - failed}/{results.Count} PASS");
return failed == 0 ? 0 : 1;
