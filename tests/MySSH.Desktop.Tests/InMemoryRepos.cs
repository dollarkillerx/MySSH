using MySSH.Core;

namespace MySSH.Desktop.Tests;

sealed class InMemoryHosts(params Host[] hosts) : IHostRepository
{
    readonly Dictionary<Guid, Host> _d = hosts.ToDictionary(h => h.Id);
    public Task<IReadOnlyList<Host>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Host>>([.. _d.Values]);
    public Task<Host?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_d.GetValueOrDefault(id));
    public Task SaveAsync(Host host, CancellationToken ct = default) { _d[host.Id] = host; return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken ct = default) { _d.Remove(id); return Task.CompletedTask; }
}

sealed class InMemoryIdentities(params Identity[] items) : IIdentityRepository
{
    readonly Dictionary<Guid, Identity> _d = items.ToDictionary(i => i.Id);
    public Task<IReadOnlyList<Identity>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Identity>>([.. _d.Values]);
    public Task<Identity?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_d.GetValueOrDefault(id));
    public Task<IdentitySecrets?> GetSecretsAsync(Guid id, CancellationToken ct = default) => Task.FromResult<IdentitySecrets?>(null);
    public Task SaveAsync(Identity identity, IdentitySecrets? secrets, CancellationToken ct = default) { _d[identity.Id] = identity; return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken ct = default) { _d.Remove(id); return Task.CompletedTask; }
}

sealed class InMemoryProxies(params ProxyProfile[] items) : IProxyRepository
{
    readonly Dictionary<Guid, ProxyProfile> _d = items.ToDictionary(i => i.Id);
    public Task<IReadOnlyList<ProxyProfile>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProxyProfile>>([.. _d.Values]);
    public Task<ProxyProfile?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_d.GetValueOrDefault(id));
    public Task<string?> GetPasswordAsync(Guid id, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task SaveAsync(ProxyProfile proxy, string? password, CancellationToken ct = default) { _d[proxy.Id] = proxy; return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken ct = default) { _d.Remove(id); return Task.CompletedTask; }
}
