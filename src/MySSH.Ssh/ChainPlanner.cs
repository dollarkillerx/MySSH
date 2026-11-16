using MySSH.Core;

namespace MySSH.Ssh;

/// <param name="Index">0 = first hop (the one the proxy applies to).</param>
/// <param name="Identity">Credential used for this hop; null when the host has none configured.</param>
/// <param name="Username">Host.Username ?? Identity.Username.</param>
public sealed record PlannedHop(int Index, Host Host, Identity? Identity, string? Username)
{
    /// <summary>Real host:port in known_hosts format (never a forwarded loopback port).</summary>
    public string HostPort => Host.KnownHostsKey;
}

/// <summary>An ordered connection chain: [proxy] → hops[0] → … → hops[^1] (= the target).</summary>
public sealed record ChainPlan(IReadOnlyList<PlannedHop> Hops, ProxyProfile? Proxy, ConnectionOrigin Origin)
{
    public PlannedHop Target => Hops[^1];
}

/// <summary>
/// Turns a host into a connection chain. Rules (OpenSSH ProxyJump semantics):
/// <list type="bullet">
/// <item>path(H) = H.JumpChain is empty ? [H] : path(J0) ++ J1..Jn ++ [H]. The first jump host is itself expanded (its own
/// JumpChain applies), later jump hosts are reached through the previous hop so their own JumpChain is not used.</item>
/// <item>proxy(H) = H.ProxyId ?? proxy(J0): the proxy always applies to the first physical hop only.</item>
/// <item>A host appearing twice in the path is a cycle; a JumpChain entry that does not exist is an error. Storage enforces
/// neither, so both are checked here.</item>
/// <item>Identities: the target uses Host.AiIdentityId for AI/MCP origins when set; jump hosts always use their IdentityId.</item>
/// </list>
/// </summary>
public static class ChainPlanner
{
    /// <summary>Upper bound on the expanded chain length (defensive; real chains are 1–4 hops).</summary>
    public const int MaxHops = 16;

    /// <summary>Pure path resolution. Returns hosts in connection order (target last) and the effective proxy id.</summary>
    public static async Task<(IReadOnlyList<Host> Path, Guid? ProxyId)> ResolvePathAsync(
        Host target, Func<Guid, CancellationToken, Task<Host?>> lookup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(lookup);
        var path = new List<Host>();
        var proxy = await ExpandAsync(target, lookup, path, [target.Id], ct);
        var seen = new HashSet<Guid>();
        foreach (var h in path)
        {
            if (!seen.Add(h.Id))
                throw new SshConnectException(SshFailureKind.InvalidConfiguration,
                    $"Jump chain of '{target.Label}' contains a cycle: '{h.Label}' appears more than once ({string.Join(" → ", path.Select(p => p.Label))}).");
        }
        if (path.Count > MaxHops)
            throw new SshConnectException(SshFailureKind.InvalidConfiguration, $"Jump chain of '{target.Label}' is longer than {MaxHops} hops.");
        return (path, proxy);
    }

    static async Task<Guid?> ExpandAsync(Host host, Func<Guid, CancellationToken, Task<Host?>> lookup, List<Host> path, HashSet<Guid> expanding, CancellationToken ct)
    {
        if (path.Count > MaxHops)
            throw new SshConnectException(SshFailureKind.InvalidConfiguration, $"Jump chain is longer than {MaxHops} hops.");
        if (host.JumpChain.Count == 0)
        {
            path.Add(host);
            return host.ProxyId;
        }

        var jumps = new List<Host>(host.JumpChain.Count);
        for (int i = 0; i < host.JumpChain.Count; i++)
        {
            var id = host.JumpChain[i];
            if (id == host.Id)
                throw new SshConnectException(SshFailureKind.InvalidConfiguration, $"'{host.Label}' lists itself as jump host.");
            var j = await lookup(id, ct)
                ?? throw new SshConnectException(SshFailureKind.InvalidConfiguration,
                    $"Jump host #{i + 1} of '{host.Label}' ({id}) does not exist. Edit the host's jump chain.");
            jumps.Add(j);
        }

        var first = jumps[0];
        if (!expanding.Add(first.Id))
            throw new SshConnectException(SshFailureKind.InvalidConfiguration,
                $"Jump chain cycle: '{first.Label}' is (indirectly) its own jump host.");
        var innerProxy = await ExpandAsync(first, lookup, path, expanding, ct);
        expanding.Remove(first.Id);
        for (int i = 1; i < jumps.Count; i++) path.Add(jumps[i]);
        path.Add(host);
        return host.ProxyId ?? innerProxy;
    }

    /// <summary>Resolves hosts, identities and the proxy into a <see cref="ChainPlan"/>.</summary>
    public static async Task<ChainPlan> PlanAsync(Guid hostId, ConnectionOrigin origin,
        IHostRepository hosts, IIdentityRepository identities, IProxyRepository proxies, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(proxies);
        var target = await hosts.GetAsync(hostId, ct)
            ?? throw new SshConnectException(SshFailureKind.InvalidConfiguration, $"Host {hostId} does not exist.");
        var (path, proxyId) = await ResolvePathAsync(target, (id, c) => hosts.GetAsync(id, c), ct);

        ProxyProfile? proxy = null;
        if (proxyId is Guid pid)
        {
            proxy = await proxies.GetAsync(pid, ct)
                ?? throw new SshConnectException(SshFailureKind.InvalidConfiguration, $"Proxy {pid} used by '{target.Label}' does not exist.", 0, path[0].Label);
        }

        var hops = new List<PlannedHop>(path.Count);
        for (int i = 0; i < path.Count; i++)
        {
            var h = path[i];
            bool isTarget = i == path.Count - 1;
            Guid? identityId = isTarget && HostKeyPolicy.IsStrict(origin) && h.AiIdentityId is Guid ai ? ai : h.IdentityId;
            Identity? identity = null;
            if (identityId is Guid iid)
            {
                identity = await identities.GetAsync(iid, ct)
                    ?? throw new SshConnectException(SshFailureKind.InvalidConfiguration, $"Identity {iid} of '{h.Label}' does not exist.", i, h.Label);
            }
            hops.Add(new PlannedHop(i, h, identity, h.Username ?? identity?.Username));
        }
        return new ChainPlan(hops, proxy, origin);
    }
}
