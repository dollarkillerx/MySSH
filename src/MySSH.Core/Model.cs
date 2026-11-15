namespace MySSH.Core;

/// <summary>Who may act on a host through the shared tool layer (design §6.3). Default Off: invisible to AI/MCP.</summary>
public enum AiPolicy { Off = 0, ReadOnly = 1, Ask = 2, Trusted = 3 }

public enum IdentityKind { Password = 0, PrivateKey = 1 }

public enum ProxyKind { Http = 0, Socks5 = 1 }

/// <summary>An SSH destination (design §3). Holds no secrets; credentials live in <see cref="Identity"/>.</summary>
public sealed record Host
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Label { get; init; }
    public required string Hostname { get; init; }
    public int Port { get; init; } = 22;
    /// <summary>Overrides <see cref="Identity.Username"/> when set.</summary>
    public string? Username { get; init; }
    public Guid? IdentityId { get; init; }
    public Guid? GroupId { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public Guid? ProxyId { get; init; }
    /// <summary>Jump hosts in connection order (first hop first). Replaces the legacy single jump_host.</summary>
    public IReadOnlyList<Guid> JumpChain { get; init; } = [];
    public int KeepAliveSeconds { get; init; } = 30;
    public string Encoding { get; init; } = "utf-8";
    public AiPolicy AiPolicy { get; init; } = AiPolicy.Off;
    /// <summary>Optional low-privilege identity used for AI/MCP actions instead of <see cref="IdentityId"/>.</summary>
    public Guid? AiIdentityId { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>known_hosts key: "host" for port 22, "[host]:port" otherwise (OpenSSH format).</summary>
    public string KnownHostsKey => KnownHostEntry.FormatHostPort(Hostname, Port);
}

/// <summary>Reusable credential (design §3). Secret material is fetched separately via <see cref="IIdentityRepository.GetSecretsAsync"/>.</summary>
public sealed record Identity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Label { get; init; }
    public string? Username { get; init; }
    public IdentityKind Kind { get; init; }
    public bool HasPassphrase { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Decrypted secret material of an <see cref="Identity"/>. Never log, never send to AI/MCP.</summary>
public sealed record IdentitySecrets(string? Password, string? PrivateKey, string? Passphrase)
{
    public override string ToString() => "IdentitySecrets { *** }";
}

public sealed record ProxyProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Label { get; init; }
    public ProxyKind Kind { get; init; }
    public required string Hostname { get; init; }
    public int Port { get; init; }
    public string? Username { get; init; }
    public bool HasPassword { get; init; }
}

public sealed record HostGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public Guid? ParentId { get; init; }
    public int Sort { get; init; }
}

/// <summary>A trusted host key (TOFU, design §5 / §8). Keyed by the REAL host:port, never a forwarded loopback port.</summary>
public sealed record KnownHostEntry
{
    public required string HostPort { get; init; }
    public required string KeyType { get; init; }
    /// <summary>OpenSSH format: "SHA256:" + unpadded base64.</summary>
    public required string Sha256Fingerprint { get; init; }
    /// <summary>Raw public key blob (base64), so an OpenSSH known_hosts line can be exported.</summary>
    public string? PublicKeyBase64 { get; init; }
    public DateTimeOffset FirstSeen { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;

    public static string FormatHostPort(string host, int port) => port == 22 ? host : $"[{host}]:{port}";
}
