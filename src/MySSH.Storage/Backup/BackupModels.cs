using System.Text.Json;
using MySSH.Core;

namespace MySSH.Storage;

// Backup v2 payload. Explicit DTOs (not the Core records) so the file format stays stable when the domain model evolves.

internal sealed class BackupPayload
{
    public string Format { get; set; } = "myssh-backup";
    public int FormatVersion { get; set; } = 2;
    public DateTimeOffset CreatedAt { get; set; }
    public bool IncludesAiProviderKeys { get; set; }
    public List<BackupGroup> Groups { get; set; } = [];
    public List<BackupIdentity> Identities { get; set; } = [];
    public List<BackupProxy> Proxies { get; set; } = [];
    public List<BackupHost> Hosts { get; set; } = [];
    public List<BackupKnownHost> KnownHosts { get; set; } = [];
    public Dictionary<string, JsonElement> Settings { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class BackupGroup
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public Guid? ParentId { get; set; }
    public int Sort { get; set; }
}

internal sealed class BackupIdentity
{
    public Guid Id { get; set; }
    public string? Label { get; set; }
    public string? Username { get; set; }
    public IdentityKind Kind { get; set; }
    public string? Password { get; set; }
    public string? PrivateKey { get; set; }
    public string? Passphrase { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class BackupProxy
{
    public Guid Id { get; set; }
    public string? Label { get; set; }
    public ProxyKind Kind { get; set; }
    public string? Hostname { get; set; }
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

internal sealed class BackupHost
{
    public Guid Id { get; set; }
    public string? Label { get; set; }
    public string? Hostname { get; set; }
    public int Port { get; set; } = 22;
    public string? Username { get; set; }
    public Guid? IdentityId { get; set; }
    public Guid? GroupId { get; set; }
    public List<string>? Tags { get; set; }
    public Guid? ProxyId { get; set; }
    public List<Guid>? JumpChain { get; set; }
    public int KeepAliveSeconds { get; set; } = 30;
    public string? Encoding { get; set; }
    public AiPolicy AiPolicy { get; set; }
    public Guid? AiIdentityId { get; set; }
    /// <summary>Added in P3; absent (null) in older v2 backups → empty (built-in defaults).</summary>
    public List<string>? AiCommandAllowlist { get; set; }
    /// <summary>Added with RDP hosts; absent in older backups → SSH.</summary>
    public HostProtocol Protocol { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class BackupKnownHost
{
    public string? HostPort { get; set; }
    public string? KeyType { get; set; }
    public string? Sha256Fingerprint { get; set; }
    public string? PublicKeyBase64 { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
