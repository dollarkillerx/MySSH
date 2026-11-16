using System.Text.Json.Serialization;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Mcp;
using MySSH.Tools;

namespace MySSH.Desktop.Services;

/// <summary>Persisted MCP switch and port (the token lives in the vault-encrypted secret store).</summary>
public sealed record McpPreferences
{
    public const string Key = "mcp.server";
    public const string TokenKey = "mcp.token";
    /// <summary>Off by default (design §6.4).</summary>
    public bool Enabled { get; init; }
    /// <summary>0 until the first start picks a random port; fixed afterwards so client configurations keep working.</summary>
    public int Port { get; init; }
}

[JsonSerializable(typeof(McpPreferences))]
internal sealed partial class McpJsonContext : JsonSerializerContext;

/// <summary>
/// <see cref="IMcpController"/> over <see cref="McpServerHost"/>. The host always starts and stops on the thread pool
/// (spike G2); <see cref="Changed"/> is coalesced because client activity fires on every call.
/// </summary>
public sealed class McpController : IMcpController, IAsyncDisposable
{
    readonly ISettingsStore _settings;
    readonly ISecretSettingsStore _secrets;
    readonly IToolCatalog _catalog;
    readonly IToolInvoker _invoker;
    readonly Func<McpHostOptions, McpHostOptions> _configure;
    readonly SemaphoreSlim _gate = new(1, 1);
    McpServerHost? _host;
    McpPreferences _prefs = new();
    int _changePending;

    /// <param name="configure">Tests override socket path / port.</param>
    public McpController(ISettingsStore settings, ISecretSettingsStore secrets, IToolCatalog catalog, IToolInvoker invoker,
        Func<McpHostOptions, McpHostOptions>? configure = null)
    {
        _settings = settings;
        _secrets = secrets;
        _catalog = catalog;
        _invoker = invoker;
        _configure = configure ?? (o => o);
        var bridge = McpClientSetup.DefaultBridgePath();
        BridgePath = File.Exists(bridge) ? bridge : null;
    }

    public bool IsEnabled => _prefs.Enabled;
    public bool IsRunning => _host is not null;
    public string? StartError { get; private set; }
    public Uri? HttpEndpoint => _host?.HttpEndpoint;
    public string? LocalSocketPath => _host?.LocalSocketPath;
    public string? Token { get; private set; }
    public string? BridgePath { get; }
    public event Action? Changed;

    public IReadOnlyList<McpClientRow> Clients => _host?.Clients
        .Select(c => new McpClientRow(c.Id, c.Transport == McpClientTransport.Local ? "local" : "http", c.DisplayName, c.FirstSeen,
            c.LastSeen, c.CallCount, c.InFlightCalls, CanDisconnect: c.Transport == McpClientTransport.Local && c.IsConnected))
        .ToList() ?? [];

    public string ClaudeCodeCommand => _host?.HttpEndpoint is { } e && Token is { } t ? McpClientSetup.ClaudeCodeCommand(e, t) : "";
    public string ClaudeDesktopJson => BridgePath is { } b ? McpClientSetup.ClaudeDesktopConfigJson(b, _host?.LocalSocketPath) : "";
    public string GenericHttpJson => _host?.HttpEndpoint is { } e && Token is { } t ? McpClientSetup.HttpConfigJson(e, t) : "";

    /// <summary>After unlock: loads preferences and starts the server when enabled. Never throws for start failures.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _prefs = await _settings.GetAsync(McpPreferences.Key, McpJsonContext.Default.McpPreferences, ct).ConfigureAwait(false) ?? new McpPreferences();
        Token = await _secrets.GetAsync(McpPreferences.TokenKey, ct).ConfigureAwait(false);
        if (_prefs.Enabled) await StartAsync(ct).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        _prefs = _prefs with { Enabled = enabled };
        await _settings.SetAsync(McpPreferences.Key, _prefs, McpJsonContext.Default.McpPreferences, ct).ConfigureAwait(false);
        if (enabled) await StartAsync(ct).ConfigureAwait(false);
        else await StopAsync().ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task RegenerateTokenAsync(CancellationToken ct = default)
    {
        var token = McpToken.Generate();
        await _secrets.SetAsync(McpPreferences.TokenKey, token, ct).ConfigureAwait(false);
        Token = token;
        _host?.SetToken(token); // live rotation, no restart
        RaiseChanged();
    }

    public async Task DisconnectAsync(string clientId)
    {
        if (_host is { } h) await Task.Run(() => h.DisconnectAsync(clientId)).ConfigureAwait(false);
        RaiseChanged();
    }

    async Task StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host is not null) return;
            StartError = null;
            if (Token is null)
            {
                Token = McpToken.Generate();
                await _secrets.SetAsync(McpPreferences.TokenKey, Token, ct).ConfigureAwait(false);
            }
            var options = _configure(new McpHostOptions { Port = _prefs.Port, Token = Token, ServerVersion = typeof(McpController).Assembly.GetName().Version?.ToString(3) ?? "0" });
            try
            {
                var host = await Task.Run(() => McpServerHost.StartAsync(options, _catalog, _invoker, null, ct), ct).ConfigureAwait(false);
                _host = host;
                host.ClientsChanged += RaiseChanged;
                if (host.Port is { } port && port != _prefs.Port && options.Port == 0)
                {
                    _prefs = _prefs with { Port = port }; // first start: keep this port from now on
                    await _settings.SetAsync(McpPreferences.Key, _prefs, McpJsonContext.Default.McpPreferences, ct).ConfigureAwait(false);
                }
            }
            catch (McpInstanceRunningException e)
            {
                StartError = Loc.Instance.Format("mcp.err.instanceRunning", ("path", e.Address ?? ""));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                StartError = Loc.Instance.Format("mcp.err.startFailed", ("error", e.Message));
            }
        }
        finally { _gate.Release(); }
    }

    async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_host is not { } h) return;
            _host = null;
            h.ClientsChanged -= RaiseChanged;
            await Task.Run(() => h.DisposeAsync().AsTask()).ConfigureAwait(false);
            StartError = null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>At most one Changed per 250 ms; client activity fires on every tool call.</summary>
    void RaiseChanged()
    {
        if (Interlocked.Exchange(ref _changePending, 1) == 1) return;
        _ = Task.Delay(250).ContinueWith(_ =>
        {
            Volatile.Write(ref _changePending, 0);
            Changed?.Invoke();
        }, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
