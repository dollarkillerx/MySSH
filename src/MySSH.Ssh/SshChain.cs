using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using MySSH.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MySSH.Ssh;

internal sealed class HopRuntime(PlannedHop hop)
{
    public PlannedHop Hop { get; } = hop;
    public AuthenticationMethod[] Auth { get; set; } = [];
    /// <summary>The key verified on the first connect to this hop; later connections of the same chain must present it.</summary>
    public ObservedHostKey? Pinned { get; set; }
    public SshClient? Client { get; set; }
    /// <summary>Endpoint (on the previous hop) that <see cref="Client"/> is connected through.</summary>
    public HopEndpoint? Endpoint { get; set; }
}

/// <summary>Everything a chain needs from the connector.</summary>
internal sealed record ChainDependencies(
    IIdentityRepository Identities, IProxyRepository Proxies, IKnownHostStore KnownHosts,
    IHostKeyVerifier Verifier, ICredentialPrompt Prompt, SshConnectorOptions Options, IHopTransport Transport);

/// <summary>
/// A live chain [proxy] → jump… → target (design §8). Owns every client and tunnel and tears them down in reverse order.
/// Host keys: two-phase TOFU — the handshake handler only compares against keys fetched beforehand (never blocks the SSH.NET
/// thread); an unknown/changed key aborts the handshake, the verifier is asked asynchronously, and the hop is reconnected
/// with the accepted key pinned (it must be byte-identical, otherwise the connect fails).
/// </summary>
internal sealed class SshChain : IPooledChain
{
    readonly ChainPlan _plan;
    readonly ChainDependencies _deps;
    readonly List<HopRuntime> _hops;
    readonly List<IDisposable> _owned = [];
    readonly List<IDisposable> _children = [];
    readonly Lock _lock = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly Encoding _encoding;
    string? _proxyPassword;
    SshConnectionState _state = SshConnectionState.Connecting;
    string? _reason;
    long _lastActivity = Stopwatch.GetTimestamp();
    Task? _heartbeat;
    KeepAlivePinger? _pinger;
    int _disposed;

    SshChain(ChainPlan plan, ChainDependencies deps)
    {
        _plan = plan;
        _deps = deps;
        _hops = [.. plan.Hops.Select(h => new HopRuntime(h))];
        _encoding = ResolveEncoding(plan.Target.Host.Encoding);
    }

    public ChainPlan Plan => _plan;
    /// <summary>Serializes channel opens on the target session (lets the shell identify its channel number).</summary>
    public SemaphoreSlim ChannelOpenGate { get; } = new(1, 1);
    public Host Host => _plan.Target.Host;
    public Encoding Encoding => _encoding;
    public SshConnectorOptions Options => _deps.Options;
    public bool TransientTrust { get; private set; }
    public SshConnectionState State { get { lock (_lock) return _state; } }
    public string? Reason { get { lock (_lock) return _reason; } }
    public CancellationToken Lifetime => _lifetime.Token;
    public SshClient Target => _hops[^1].Client ?? throw new InvalidOperationException("Chain is not connected.");
    internal IReadOnlyList<HopRuntime> Hops => _hops;

    /// <summary>Raised on a background thread.</summary>
    public event Action<SshConnectionStateChange>? StateChanged;

    public bool IsReusableFor(ConnectionOrigin origin)
        => State == SshConnectionState.Connected && (origin == ConnectionOrigin.User || !TransientTrust);

    public void MarkActivity() => Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());

    public void ThrowIfNotConnected()
    {
        lock (_lock)
        {
            if (_state == SshConnectionState.Connected) return;
            throw new SshConnectionLostException(_state == SshConnectionState.Closed
                ? "The SSH connection is closed."
                : $"The SSH connection to '{Host.Label}' was lost{(_reason is null ? "" : ": " + _reason)}.");
        }
    }

    static Encoding ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new UTF8Encoding(false);
        try { return Encoding.GetEncoding(name); }
        catch (ArgumentException) { return new UTF8Encoding(false); }
    }

    // ------------------------------------------------------------------ connect

    public static async Task<SshChain> ConnectAsync(ChainPlan plan, ChainDependencies deps, CancellationToken ct)
    {
        var chain = new SshChain(plan, deps);
        try
        {
            await chain.ConnectAllAsync(ct);
            return chain;
        }
        catch
        {
            await chain.DisposeAsync();
            throw;
        }
    }

    async Task ConnectAllAsync(CancellationToken ct)
    {
        if (_plan.Proxy is { HasPassword: true } proxy)
            _proxyPassword = await _deps.Proxies.GetPasswordAsync(proxy.Id, ct);

        for (int i = 0; i < _hops.Count; i++)
        {
            var h = _hops[i];
            h.Auth = await Credentials.BuildAsync(h.Hop, _plan, _deps.Identities, _deps.Prompt, _deps.Options.MaxPassphraseAttempts, _owned, ct);
            var (client, endpoint) = await ConnectClientAsync(i, ci => new SshClient(ci), ct);
            h.Client = client;
            h.Endpoint = endpoint;
            var keepAlive = _deps.Options.KeepAliveOverride ?? TimeSpan.FromSeconds(h.Hop.Host.KeepAliveSeconds);
            if (keepAlive > TimeSpan.Zero) client.KeepAliveInterval = keepAlive;
            var label = h.Hop.Host.Label;
            client.ErrorOccurred += (_, e) => OnLost($"{label}: {e.Exception.Message}");
        }

        bool lostWhileConnecting;
        lock (_lock)
        {
            lostWhileConnecting = _state != SshConnectionState.Connecting;
            if (!lostWhileConnecting) _state = SshConnectionState.Connected;
        }
        if (lostWhileConnecting || !_hops.TrueForAll(h => h.Client!.IsConnected))
            throw new SshConnectException(SshFailureKind.Network, $"The connection to '{Host.Label}' dropped while connecting{(_reason is null ? "" : ": " + _reason)}.");

        if (SshNetInternals.IsGlobalRequestSupported && SshNetInternals.TryGetSession(Target) is { } session)
            _pinger = new KeepAlivePinger(session);
        if (_deps.Options.HeartbeatInterval > TimeSpan.Zero)
            _heartbeat = Task.Run(() => HeartbeatLoopAsync(_lifetime.Token), CancellationToken.None);
    }

    /// <summary>Connects one client to hop <paramref name="index"/> (two-phase TOFU, see class doc).</summary>
    internal async Task<(T Client, HopEndpoint? Endpoint)> ConnectClientAsync<T>(int index, Func<ConnectionInfo, T> make, CancellationToken ct)
        where T : BaseClient
    {
        var h = _hops[index];
        var hop = h.Hop;
        var label = hop.Host.Label;
        IReadOnlyList<KnownHostEntry> stored = h.Pinned is null ? await _deps.KnownHosts.FindAsync(hop.HostPort, ct) : [];

        for (int attempt = 0; ; attempt++)
        {
            HopEndpoint? endpoint = null;
            T? client = null;
            var gate = new HostKeyGate(stored, h.Pinned);
            try
            {
                string host = hop.Host.Hostname;
                int port = hop.Host.Port;
                if (index > 0)
                {
                    var via = _hops[index - 1].Client ?? throw new InvalidOperationException("previous hop not connected");
                    endpoint = await _deps.Transport.OpenAsync(via, host, port, OnViolation, ct);
                    (host, port) = (endpoint.Host, endpoint.Port);
                }
                var ci = MakeConnectionInfo(h, host, port, index == 0, stored);
                client = make(ci);
                client.HostKeyReceived += (_, e) => e.CanTrust = gate.Check(e.HostKeyName, e.HostKey);

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(_deps.Options.ConnectTimeout + TimeSpan.FromSeconds(2));
                    await client.ConnectAsync(timeout.Token);
                }
                ci.Timeout = _deps.Options.OperationTimeout;
                h.Pinned ??= gate.Observed;
                return (client, endpoint);
            }
            catch (Exception ex)
            {
                client?.Dispose();
                endpoint?.Dispose();
                ct.ThrowIfCancellationRequested();
                if (ex is SshConnectException) throw;
                if (gate.Observed is { Evaluation.Match: not HostKeyMatch.Match } observed)
                {
                    if (h.Pinned is not null || attempt > 0)
                        throw new SshConnectException(SshFailureKind.HostKeyRejected,
                            $"Host key of {hop.HostPort} changed while connecting (expected {(h.Pinned ?? observed).Fingerprint}, got {observed.Fingerprint}).", index, label, ex);
                    TransientTrust |= await HostKeyPolicy.ResolveAsync(observed, hop.HostPort, label, index, _hops.Count, _plan.Origin, _deps.Verifier, _deps.KnownHosts, ct);
                    h.Pinned = observed with { Evaluation = new HostKeyEvaluation(HostKeyMatch.Match, null) };
                    continue;
                }
                throw MapConnectError(ex, index, host: hop.HostPort);
            }
        }
    }

    ConnectionInfo MakeConnectionInfo(HopRuntime h, string host, int port, bool first, IReadOnlyList<KnownHostEntry> stored)
    {
        var user = h.Hop.Username!;
        ConnectionInfo ci;
        if (first && _plan.Proxy is { } p)
        {
            var type = p.Kind == ProxyKind.Socks5 ? ProxyTypes.Socks5 : ProxyTypes.Http;
            ci = new ConnectionInfo(host, port, user, type, p.Hostname, p.Port, p.Username!, _proxyPassword!, h.Auth);
        }
        else
        {
            ci = new ConnectionInfo(host, port, user, h.Auth);
        }
        ci.Timeout = _deps.Options.ConnectTimeout;
        ci.Encoding = _encoding;
        if (_deps.Options.LoggerFactory is { } lf) ci.LoggerFactory = lf;
        // Put key types we already trust first so an honest server presents the stored one.
        var preferred = h.Pinned is { } pin ? [pin.KeyType] : stored.Select(s => s.KeyType).Distinct().Reverse().ToArray();
        foreach (var type in preferred)
            if (ci.HostKeyAlgorithms.ContainsKey(type)) ci.HostKeyAlgorithms.SetPosition(type, 0);
        return ci;
    }

    SshConnectException MapConnectError(Exception ex, int index, string host)
    {
        var label = _hops[index].Hop.Host.Label;
        var viaProxy = index == 0 && _plan.Proxy is not null;
        return ex switch
        {
            SshAuthenticationException => new(SshFailureKind.AuthenticationFailed,
                $"Authentication as '{_hops[index].Hop.Username}' on {host} ('{label}') failed: {ex.Message}", index, label, ex),
            ProxyException => new(SshFailureKind.Proxy, $"Proxy '{_plan.Proxy?.Label}' could not open a tunnel to {host}: {ex.Message}", index, label, ex),
            SocketException when viaProxy => new(SshFailureKind.Proxy,
                $"Cannot reach proxy '{_plan.Proxy!.Label}' ({_plan.Proxy.Hostname}:{_plan.Proxy.Port}): {ex.Message}", index, label, ex),
            SocketException => new(SshFailureKind.Network, $"Cannot reach {host} ('{label}'): {ex.Message}", index, label, ex),
            SshOperationTimeoutException or TimeoutException or OperationCanceledException => new(SshFailureKind.Timeout,
                $"Connecting to {host} ('{label}') timed out.", index, label, ex),
            SshConnectionException => new(SshFailureKind.Network, $"Connection to {host} ('{label}') failed: {ex.Message}", index, label, ex),
            _ => new(SshFailureKind.Protocol, $"Connecting to {host} ('{label}') failed: {ex.Message}", index, label, ex),
        };
    }

    /// <summary>Opens an additional connection to the target through the same chain (SSH.NET's SftpClient needs its own session).</summary>
    public async Task<(T Client, IDisposable Owner)> ConnectExtraTargetClientAsync<T>(Func<ConnectionInfo, T> make, CancellationToken ct) where T : BaseClient
    {
        ThrowIfNotConnected();
        var (client, endpoint) = await ConnectClientAsync(_hops.Count - 1, make, ct);
        var owner = new ChildHandle(this, client, endpoint);
        lock (_lock)
        {
            if (_state != SshConnectionState.Connected)
            {
                owner.Dispose();
                ThrowIfNotConnected();
            }
            _children.Add(owner);
        }
        return (client, owner);
    }

    sealed class ChildHandle(SshChain chain, BaseClient client, HopEndpoint? endpoint) : IDisposable
    {
        int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (chain._lock) chain._children.Remove(this);
            SafeDisconnect(client);
            endpoint?.Dispose();
        }
    }

    // ------------------------------------------------------------------ liveness

    void OnViolation(string message)
    {
        OnLost(message);
    }

    internal void OnLost(string reason)
    {
        SshConnectionStateChange change;
        lock (_lock)
        {
            if (_state is SshConnectionState.Lost or SshConnectionState.Closed) return;
            var previous = _state;
            _state = SshConnectionState.Lost;
            _reason = reason;
            change = new SshConnectionStateChange(previous, SshConnectionState.Lost, reason);
        }
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        if (change.Previous == SshConnectionState.Connected) Raise(change);
        _ = Task.Run(TearDown);
    }

    void Raise(SshConnectionStateChange change)
    {
        try { StateChanged?.Invoke(change); }
#pragma warning disable CA1031 // subscriber exceptions must not kill SSH.NET or the heartbeat thread
        catch (Exception) { }
#pragma warning restore CA1031
    }

    async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var interval = _deps.Options.HeartbeatInterval;
        var tick = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(200).Ticks, interval.Ticks / 4));
        using var timer = new PeriodicTimer(tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActivity)) < interval) continue;
                bool alive = await ProbeAsync(ct);
                if (ct.IsCancellationRequested) return;
                if (!alive)
                {
                    OnLost($"'{Host.Label}' did not answer a heartbeat within {_deps.Options.HeartbeatTimeout.TotalSeconds:0.#} s.");
                    return;
                }
                MarkActivity();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// App-level heartbeat: <c>keepalive@openssh.com</c> with want-reply (any reply proves the whole path is alive, like
    /// OpenSSH ServerAliveInterval). Falls back to exec <c>true</c> when the SSH.NET internals are unavailable.
    /// </summary>
    internal async Task<bool> ProbeAsync(CancellationToken ct)
    {
        var timeout = _deps.Options.HeartbeatTimeout;
        try
        {
            if (_pinger is { } pinger) return await pinger.PingAsync(timeout, ct);
            var target = Target;
            var probe = Task.Run(async () =>
            {
                using var cmd = target.CreateCommand("true");
                cmd.CommandTimeout = timeout;
                Task exec;
                await ChannelOpenGate.WaitAsync(ct);
                try { exec = cmd.ExecuteAsync(CancellationToken.None); }
                finally { ChannelOpenGate.Release(); }
                try { await exec; }
                catch (SshException ex) when (ex is not SshOperationTimeoutException and not SshConnectionException) { /* answered */ }
            }, CancellationToken.None);
            _ = probe.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await probe.WaitAsync(timeout, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is TimeoutException or SshException or ObjectDisposedException or InvalidOperationException or SocketException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ teardown

    void TearDown()
    {
        _pinger?.Dispose();
        IDisposable[] children;
        lock (_lock) children = [.. _children];
        foreach (var c in children) c.Dispose();
        for (int i = _hops.Count - 1; i >= 0; i--)
        {
            var h = _hops[i];
            if (h.Client is { } client) SafeDisconnect(client);
            h.Endpoint?.Dispose();
        }
    }

    static void SafeDisconnect(BaseClient client)
    {
        try { if (client.IsConnected) client.Disconnect(); }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException or SocketException) { }
        try { client.Dispose(); }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException or SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SshConnectionStateChange? change = null;
        lock (_lock)
        {
            if (_state != SshConnectionState.Closed)
            {
                change = _state == SshConnectionState.Connecting ? null : new SshConnectionStateChange(_state, SshConnectionState.Closed, null);
                _state = SshConnectionState.Closed;
            }
        }
        try { await _lifetime.CancelAsync(); } catch (ObjectDisposedException) { }
        if (change is not null) Raise(change);
        // Bounded: a frozen peer must never hang the caller (SSH.NET's Disconnect/ForwardedPort.Stop wait on the network).
        try { await Task.Run(TearDown).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { }
        if (_heartbeat is { } hb) await hb.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (var d in _owned)
        {
            try { d.Dispose(); } catch (ObjectDisposedException) { }
        }
        _lifetime.Dispose();
        ChannelOpenGate.Dispose();
    }
}

/// <summary>Sends keepalive@openssh.com and waits for SSH_MSG_REQUEST_SUCCESS/FAILURE (either proves liveness).</summary>
internal sealed class KeepAlivePinger : IDisposable
{
    readonly Session _session;
    readonly Lock _lock = new();
    TaskCompletionSource? _pending;

    public KeepAlivePinger(Session session)
    {
        _session = session;
        session.RegisterMessage("SSH_MSG_REQUEST_SUCCESS");
        session.RegisterMessage("SSH_MSG_REQUEST_FAILURE");
        session.RequestSuccessReceived += OnReply;
        session.RequestFailureReceived += OnReply;
    }

    void OnReply<TArgs>(object? sender, TArgs e)
    {
        TaskCompletionSource? p;
        lock (_lock) { p = _pending; _pending = null; }
        p?.TrySetResult();
    }

    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _pending = tcs;
        // SendMessage takes SSH.NET's socket write lock: if the peer is frozen and a large write holds it, it blocks.
        var send = Task.Run(() => SshNetInternals.SendKeepAliveRequest(_session), CancellationToken.None);
        _ = send.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try
        {
            await Task.WhenAll(send, tcs.Task).WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException or SocketException) { return false; }
    }

    public void Dispose()
    {
        _session.RequestSuccessReceived -= OnReply;
        _session.RequestFailureReceived -= OnReply;
    }
}
