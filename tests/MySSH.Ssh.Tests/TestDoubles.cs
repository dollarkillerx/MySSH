using System.Collections.Concurrent;
using MySSH.Core;

namespace MySSH.Ssh.Tests;

sealed class MemHosts : IHostRepository
{
    readonly ConcurrentDictionary<Guid, Host> _d = new();
    public MemHosts(params Host[] hosts) { foreach (var h in hosts) _d[h.Id] = h; }
    public Host Add(Host h) { _d[h.Id] = h; return h; }
    public Task<IReadOnlyList<Host>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Host>>([.. _d.Values]);
    public Task<Host?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_d.GetValueOrDefault(id));
    public Task SaveAsync(Host host, CancellationToken ct = default) { _d[host.Id] = host; return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken ct = default) { _d.TryRemove(id, out _); return Task.CompletedTask; }
}

sealed class MemIdentities : IIdentityRepository
{
    readonly ConcurrentDictionary<Guid, (Identity Identity, IdentitySecrets? Secrets)> _d = new();
    public Identity Add(Identity i, IdentitySecrets? s) { _d[i.Id] = (i, s); return i; }
    public Task<IReadOnlyList<Identity>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Identity>>([.. _d.Values.Select(v => v.Identity)]);
    public Task<Identity?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_d.TryGetValue(id, out var v) ? v.Identity : null);
    public Task<IdentitySecrets?> GetSecretsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_d.TryGetValue(id, out var v) ? v.Secrets : null);
    public Task SaveAsync(Identity identity, IdentitySecrets? secrets, CancellationToken ct = default) { _d[identity.Id] = (identity, secrets ?? _d.GetValueOrDefault(identity.Id).Secrets); return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken ct = default) { _d.TryRemove(id, out _); return Task.CompletedTask; }
}

sealed class MemProxies : IProxyRepository
{
    readonly ConcurrentDictionary<Guid, (ProxyProfile Proxy, string? Password)> _d = new();
    public ProxyProfile Add(ProxyProfile p, string? password = null) { _d[p.Id] = (p, password); return p; }
    public Task<IReadOnlyList<ProxyProfile>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProxyProfile>>([.. _d.Values.Select(v => v.Proxy)]);
    public Task<ProxyProfile?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_d.TryGetValue(id, out var v) ? v.Proxy : null);
    public Task<string?> GetPasswordAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_d.TryGetValue(id, out var v) ? v.Password : null);
    public Task SaveAsync(ProxyProfile proxy, string? password, CancellationToken ct = default) { _d[proxy.Id] = (proxy, password); return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken ct = default) { _d.TryRemove(id, out _); return Task.CompletedTask; }
}

sealed class MemKnownHosts : IKnownHostStore
{
    readonly ConcurrentDictionary<(string, string), KnownHostEntry> _d = new();
    public int Upserts;
    public Task<IReadOnlyList<KnownHostEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<KnownHostEntry>>([.. _d.Values]);
    public Task<IReadOnlyList<KnownHostEntry>> FindAsync(string hostPort, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<KnownHostEntry>>([.. _d.Values.Where(e => e.HostPort == hostPort)]);
    public Task UpsertAsync(KnownHostEntry entry, CancellationToken ct = default) { Interlocked.Increment(ref Upserts); _d[(entry.HostPort, entry.KeyType)] = entry; return Task.CompletedTask; }
    public Task RemoveAsync(string hostPort, string keyType, CancellationToken ct = default) { _d.TryRemove((hostPort, keyType), out _); return Task.CompletedTask; }
}

/// <summary>Records every check and answers with a scripted decision.</summary>
sealed class ScriptedVerifier(Func<HostKeyCheck, HostKeyDecision> decide) : IHostKeyVerifier
{
    public readonly ConcurrentQueue<HostKeyCheck> Checks = new();
    public ScriptedVerifier(HostKeyDecision always) : this(_ => always) { }
    public ValueTask<HostKeyDecision> VerifyAsync(HostKeyCheck check, CancellationToken ct)
    {
        Checks.Enqueue(check);
        return ValueTask.FromResult(decide(check));
    }
}

sealed class ScriptedPrompt(params string?[] answers) : ICredentialPrompt
{
    readonly Queue<string?> _answers = new(answers);
    public readonly List<string?> Errors = [];
    public ValueTask<string?> PromptPassphraseAsync(Identity identity, string? previousAttemptError, CancellationToken ct)
    {
        lock (Errors) Errors.Add(previousAttemptError);
        return ValueTask.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
    }
}
