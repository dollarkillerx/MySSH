using MySSH.Core;

namespace MySSH.Ssh;

public interface ISshConnector
{
    /// <summary>
    /// Connects (or reuses a pooled chain for) a host. User origin may prompt for host keys and passphrases; Ai/Mcp origins
    /// are strict (unknown/changed host key → <see cref="SshConnectException"/>, never prompt) and use Host.AiIdentityId when set.
    /// </summary>
    Task<ISshConnection> ConnectAsync(Guid hostId, ConnectionOrigin origin, CancellationToken ct = default);
}

public sealed class SshConnector : ISshConnector, IAsyncDisposable
{
    readonly IHostRepository _hosts;
    readonly IIdentityRepository _identities;
    readonly IProxyRepository _proxies;
    readonly ChainDependencies _deps;
    readonly ConnectionPool<SshChain> _pool = new();

    public SshConnector(
        IHostRepository hosts, IIdentityRepository identities, IProxyRepository proxies, IKnownHostStore knownHosts,
        IHostKeyVerifier hostKeyVerifier, ICredentialPrompt credentialPrompt, SshConnectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(proxies);
        ArgumentNullException.ThrowIfNull(knownHosts);
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);
        ArgumentNullException.ThrowIfNull(credentialPrompt);
        _hosts = hosts;
        _identities = identities;
        _proxies = proxies;
        Options = options ?? new SshConnectorOptions();
        var transport = Options.CustomHopTransport ?? (Options.HopTransport == HopTransportKind.SingleAcceptBridge
            ? new SingleAcceptBridgeTransport()
            : new ForwardedPortHopTransport());
        _deps = new ChainDependencies(identities, proxies, knownHosts, hostKeyVerifier, credentialPrompt, Options, transport);
    }

    public SshConnectorOptions Options { get; }

    /// <summary>Number of live pooled chains (diagnostics).</summary>
    public int PooledConnectionCount => _pool.EntryCount;

    public async Task<ISshConnection> ConnectAsync(Guid hostId, ConnectionOrigin origin, CancellationToken ct = default)
    {
        var plan = await ChainPlanner.PlanAsync(hostId, origin, _hosts, _identities, _proxies, ct);
        var key = new PoolKey(hostId, plan.Target.Identity?.Id);
        var poolRef = await _pool.AcquireAsync(key, origin, c => SshChain.ConnectAsync(plan, _deps, c), ct);
        return new SshConnectionLease(poolRef, origin);
    }

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}
