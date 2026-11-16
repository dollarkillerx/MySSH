using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using MySSH.Tools;

namespace MySSH.Mcp;

/// <summary>
/// The in-process MCP server of the desktop app (design §6.4): Streamable HTTP on 127.0.0.1 (stateless) and/or the local
/// socket for the <c>myssh-mcp</c> stdio bridge. All tool logic, policy and approvals live behind <see cref="IToolInvoker"/>.
/// <para>Threading: <see cref="StartAsync"/> does all its work on the thread pool and never blocks the caller (UI) thread;
/// tool calls run on the thread pool; <see cref="ClientsChanged"/> is raised on thread-pool threads.</para>
/// </summary>
public sealed partial class McpServerHost : IAsyncDisposable
{
    /// <summary>HTTP endpoint path.</summary>
    public const string EndpointPath = "/mcp";

    private readonly ClientTracker _tracker;
    private readonly ILogger _logger;
    private WebApplication? _app;
    private HttpSecurity? _security;
    private LocalSocketListener? _local;
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private bool _stopped;

    private McpServerHost(ClientTracker tracker, ILogger logger, string token)
    {
        _tracker = tracker;
        _logger = logger;
        Token = token;
        tracker.Changed += () => ClientsChanged?.Invoke();
    }

    /// <summary>Default local endpoint: the macOS/Linux socket path or the Windows pipe (<c>\\.\pipe\myssh-mcp-{user}</c>).</summary>
    public static string DefaultLocalSocketPath => LocalEndpoint.DefaultAddress();

    /// <summary><c>http://127.0.0.1:{port}/mcp</c>, or null when HTTP is disabled.</summary>
    public Uri? HttpEndpoint { get; private set; }

    /// <summary>The bound TCP port (useful with <see cref="McpHostOptions.Port"/> = 0), or null when HTTP is disabled.</summary>
    public int? Port { get; private set; }

    /// <summary>The bearer token HTTP clients must send (from the options, or generated).</summary>
    public string Token { get; private set; }

    /// <summary>The socket path (or Windows pipe address) being served, or null when the local socket is disabled.</summary>
    public string? LocalSocketPath => _local?.Address;

    /// <summary>Recently active clients, most recent first. See <see cref="McpClientInfo"/> for how HTTP clients are grouped.</summary>
    public IReadOnlyList<McpClientInfo> Clients => _tracker.Snapshot();

    /// <summary>
    /// Raised (on a thread-pool thread, possibly many times per second while tools run) whenever <see cref="Clients"/>
    /// changes. Marshal to the UI thread and coalesce.
    /// </summary>
    public event Action? ClientsChanged;

    /// <summary>
    /// Starts the enabled transports on the thread pool. The local socket is bound first, so a second app instance fails
    /// fast with <see cref="McpInstanceRunningException"/> before touching the HTTP port.
    /// </summary>
    /// <exception cref="McpInstanceRunningException">Another process serves the local socket / pipe.</exception>
    /// <exception cref="IOException">The HTTP port is in use (Kestrel), or the socket directory cannot be secured.</exception>
    /// <exception cref="PathTooLongException">The socket path exceeds the OS sun_path limit.</exception>
    public static Task<McpServerHost> StartAsync(McpHostOptions options, IToolCatalog catalog, IToolInvoker invoker,
        ILoggerFactory? loggerFactory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(invoker);
        if (!options.EnableHttp && !options.EnableLocalSocket)
            throw new ArgumentException("enable at least one of EnableHttp / EnableLocalSocket", nameof(options));
        if (options.Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), options.Port, "Port must be 0..65535");
        // Never run Kestrel / socket setup on the caller's (UI) thread.
        return Task.Run(() => StartCoreAsync(options, catalog, invoker, loggerFactory, ct), ct);
    }

    private static async Task<McpServerHost> StartCoreAsync(McpHostOptions options, IToolCatalog catalog, IToolInvoker invoker,
        ILoggerFactory? callerLoggerFactory, CancellationToken ct)
    {
        var loggerFactory = new FilteringLoggerFactory(callerLoggerFactory);
        var tracker = new ClientTracker(TimeProvider.System);
        var token = string.IsNullOrEmpty(options.Token) ? McpToken.Generate() : options.Token;
        var host = new McpServerHost(tracker, loggerFactory.CreateLogger("MySSH.Mcp"), token);
        var adapter = new McpToolAdapter(catalog, invoker, tracker, loggerFactory.CreateLogger("MySSH.Mcp.Tools"));
        try
        {
            if (options.EnableLocalSocket)
            {
                var address = string.IsNullOrEmpty(options.LocalSocketPath) ? LocalEndpoint.DefaultAddress() : options.LocalSocketPath;
                host._local = await LocalSocketListener.StartAsync(address, entry =>
                {
                    var serverOptions = new McpServerOptions();
                    adapter.Configure(serverOptions, options, entry);
                    return serverOptions;
                }, loggerFactory, tracker, ct).ConfigureAwait(false);
            }
            if (options.EnableHttp)
                await host.StartHttpAsync(options, adapter, callerLoggerFactory, ct).ConfigureAwait(false);
            return host;
        }
        catch
        {
            await host.StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartHttpAsync(McpHostOptions options, McpToolAdapter adapter, ILoggerFactory? callerLoggerFactory, CancellationToken ct)
    {
        // CreateSlimBuilder: no IIS / HostFiltering / HTTPS / static files (smaller start-up; Host is checked by HttpSecurity).
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "MySSH.Mcp",
        });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, options.Port); // 127.0.0.1 only; port 0 -> OS picks
            k.AddServerHeader = false;
        });
        // Default 30 s would hold app exit hostage to a pending approval (spike G2).
        builder.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(3));
        // The default ConsoleLifetime would take over SIGTERM/SIGINT of the whole desktop process (and swallow them after
        // stopping only this host, spike G3). Signals belong to the app; it stops us via StopAsync.
        builder.Services.AddSingleton<IHostLifetime, NoSignalHostLifetime>();
        builder.Logging.ClearProviders();
        if (callerLoggerFactory is not null)
            builder.Logging.AddProvider(new ForwardingLoggerProvider(callerLoggerFactory));
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);

        builder.Services
            .AddMcpServer(o => adapter.Configure(o, options, local: null))
            // Stateless (design §6.4, spike G5): in stateful/hybrid mode 2025-11-25 clients cannot cancel calls.
            .WithHttpTransport(h => h.SessionMode = HttpServerSessionMode.Stateless);

        var app = builder.Build();
        var security = new HttpSecurity(Token, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MySSH.Mcp.Http"));
        app.Use(security.InvokeAsync);
        app.MapMcp(EndpointPath);

        _app = app;
        _security = security;
        await app.StartAsync(ct).ConfigureAwait(false);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var port = new Uri(address).Port;
        Port = port;
        HttpEndpoint = new Uri($"http://127.0.0.1:{port}{EndpointPath}");
        LogHttpListening(_logger, HttpEndpoint);
    }

    /// <summary>
    /// Replaces the HTTP bearer token without restarting ("regenerate token" in Settings). Requests already running are
    /// not affected; every later request needs the new token. This is also the only way to lock out an HTTP client.
    /// </summary>
    public void SetToken(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        Token = token;
        _security?.SetToken(token);
    }

    /// <summary>
    /// Disconnects a client listed in <see cref="Clients"/>. Local socket clients: closes the connection (in-flight calls are
    /// cancelled, open approval prompts close, the bridge process exits) and returns true. HTTP clients: stateless HTTP keeps
    /// no connection or session to close, so this only forgets the entry and returns false; to lock an HTTP client out,
    /// rotate the token with <see cref="SetToken"/>.
    /// </summary>
    public async Task<bool> DisconnectAsync(string clientId)
    {
        var entry = _tracker.Find(clientId);
        if (entry is null) return false;
        if (entry.Disconnect is { } disconnect)
        {
            try { await disconnect().ConfigureAwait(false); }
            catch (TimeoutException) { LogSlowDisconnect(_logger, clientId); }
            return true;
        }
        _tracker.Forget(entry);
        return false;
    }

    /// <summary>Stops both transports (in-flight calls are cancelled; at most ~3 s). Idempotent.</summary>
    public async Task StopAsync()
    {
        await _stopLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped) return;
            _stopped = true;
            if (_app is { } app)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await app.StopAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                await app.DisposeAsync().ConfigureAwait(false);
                _app = null;
            }
            if (_local is { } local)
            {
                await local.DisposeAsync().ConfigureAwait(false);
            }
            _tracker.Clear();
        }
        finally
        {
            _stopLock.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP HTTP endpoint listening on {Endpoint}")]
    private static partial void LogHttpListening(ILogger logger, Uri endpoint);
    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP client {Id} did not disconnect within 3 s")]
    private static partial void LogSlowDisconnect(ILogger logger, string id);
}

/// <summary>An <see cref="IHostLifetime"/> that does not hook process signals or the console.</summary>
internal sealed class NoSignalHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
