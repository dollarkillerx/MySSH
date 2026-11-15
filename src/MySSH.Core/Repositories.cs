namespace MySSH.Core;

/// <summary>Thrown when deleting an entity that other records still reference (e.g. a host used as a jump host).</summary>
public sealed class EntityInUseException(string message, IReadOnlyList<Guid> referencedBy) : InvalidOperationException(message)
{
    public IReadOnlyList<Guid> ReferencedBy { get; } = referencedBy;
}

public interface IHostRepository
{
    Task<IReadOnlyList<Host>> ListAsync(CancellationToken ct = default);
    Task<Host?> GetAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(Host host, CancellationToken ct = default);
    /// <summary>Throws <see cref="EntityInUseException"/> if another host's JumpChain references it.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IIdentityRepository
{
    Task<IReadOnlyList<Identity>> ListAsync(CancellationToken ct = default);
    Task<Identity?> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Decrypts on demand. Requires an unlocked vault.</summary>
    Task<IdentitySecrets?> GetSecretsAsync(Guid id, CancellationToken ct = default);
    /// <summary><paramref name="secrets"/> null = keep existing secrets.</summary>
    Task SaveAsync(Identity identity, IdentitySecrets? secrets, CancellationToken ct = default);
    /// <summary>Throws <see cref="EntityInUseException"/> if a host references it.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IProxyRepository
{
    Task<IReadOnlyList<ProxyProfile>> ListAsync(CancellationToken ct = default);
    Task<ProxyProfile?> GetAsync(Guid id, CancellationToken ct = default);
    Task<string?> GetPasswordAsync(Guid id, CancellationToken ct = default);
    /// <summary><paramref name="password"/> null = keep existing.</summary>
    Task SaveAsync(ProxyProfile proxy, string? password, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IGroupRepository
{
    Task<IReadOnlyList<HostGroup>> ListAsync(CancellationToken ct = default);
    Task SaveAsync(HostGroup group, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IKnownHostStore
{
    Task<IReadOnlyList<KnownHostEntry>> ListAsync(CancellationToken ct = default);
    /// <summary>All trusted keys for a host:port (one per key type).</summary>
    Task<IReadOnlyList<KnownHostEntry>> FindAsync(string hostPort, CancellationToken ct = default);
    Task UpsertAsync(KnownHostEntry entry, CancellationToken ct = default);
    Task RemoveAsync(string hostPort, string keyType, CancellationToken ct = default);
}
