using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace MySSH.Spike.McpHost;

/// <summary>In-process generic host (WebApplication) running next to the Avalonia UI loop.</summary>
public sealed class McpHostRuntime(WebApplication app, string endpoint, string token, string endpointFile) : IAsyncDisposable
{
    public string Endpoint { get; } = endpoint;
    public string Token { get; } = token;
    public IServiceProvider Services => app.Services;
    public IHostApplicationLifetime Lifetime => app.Lifetime;

    public static async Task<McpHostRuntime> StartAsync(SpikeOptions options, HttpServerSessionMode sessionMode)
    {
        var t0 = Program.Uptime.Elapsed;
        var wsBefore = Environment.WorkingSet;
        var token = LocalSecurity.NewToken();

        // CreateSlimBuilder: no IIS/HostFiltering/HTTPS/static files; smaller startup than CreateBuilder.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "MySSH.Spike.McpHost",
        });
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0)); // port 0 -> OS picks
        builder.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(3));
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(c => { c.SingleLine = true; c.TimestampFormat = "HH:mm:ss.fff "; });
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("ModelContextProtocol", Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("MYSSH_MCP_LOG"), out var mcpLevel) ? mcpLevel : LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<EventLog>();
        if (options.AutoApprove is { } allow)
            builder.Services.AddSingleton<IApprovalService>(new AutoApprovalService(allow));
        else
            builder.Services.AddSingleton<IApprovalService, AvaloniaApprovalService>();

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "myssh-spike", Version = "0.0.1" };
                o.ServerInstructions = "MySSH spike server. Use list_hosts first; run_command requires approval in the MySSH GUI.";
            })
            .WithHttpTransport(h =>
            {
                h.SessionMode = sessionMode; // SDK 2.4 default is already Stateless; set explicitly for clarity
            })
            .WithTools<MySshTools>(SpikeJson.ToolOptions); // AOT: source-generated JSON metadata

        builder.Services.AddHostedService<LocalSocketListener>();

        var app = builder.Build();
        var events = app.Services.GetRequiredService<EventLog>();
        app.UseLocalMcpSecurity(token, events);
        app.MapMcp("/mcp");

        await app.StartAsync();

        var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var endpoint = addr.TrimEnd('/') + "/mcp";
        var startupMs = (Program.Uptime.Elapsed - t0).TotalMilliseconds;
        var info = new EndpointInfo(
            Url: endpoint,
            Token: token,
            Socket: options.SocketPath,
            Pid: Environment.ProcessId,
            HttpSessionMode: sessionMode.ToString(),
            Approval: options.AutoApprove switch { true => "auto-allow", false => "auto-deny", null => $"ui(timeout={options.ApprovalTimeout.TotalSeconds}s)" },
            Tray: options.Tray,
            HostStartMs: Math.Round(startupMs),
            ProcessUptimeAtReadyMs: Math.Round(Program.Uptime.Elapsed.TotalMilliseconds),
            WorkingSetBeforeHostMB: Math.Round(wsBefore / 1048576.0, 1),
            WorkingSetAfterHostMB: Math.Round(Environment.WorkingSet / 1048576.0, 1),
            ClaudeCodeCommand: $"claude mcp add --transport http myssh {endpoint} --header \"Authorization: Bearer {token}\"");
        var tmp = options.EndpointFile + ".tmp";
        var jsonOpts = new JsonSerializerOptions(SpikeJsonContext.Default.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(tmp, JsonSerializer.Serialize(info, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<EndpointInfo>)jsonOpts.GetTypeInfo(typeof(EndpointInfo))));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, options.EndpointFile, overwrite: true); // atomic publish; probe polls for this file
        events.Write("HOST READY", $"{endpoint} startMs={info.HostStartMs} uptimeMs={info.ProcessUptimeAtReadyMs} ws={info.WorkingSetBeforeHostMB}->{info.WorkingSetAfterHostMB}MB mode={sessionMode} approval={info.Approval} tray={options.Tray}");
        return new McpHostRuntime(app, endpoint, token, options.EndpointFile);
    }

    public async Task StopAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await app.StopAsync(cts.Token);
        try { File.Delete(endpointFile); } catch { }
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
