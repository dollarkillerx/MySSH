namespace MySSH.Ssh;

/// <summary>Why a connection attempt failed. Stable values: the UI maps them to localized messages.</summary>
public enum SshFailureKind
{
    /// <summary>Host/identity/proxy records are missing or inconsistent (jump-chain cycle, missing hop, no username...).</summary>
    InvalidConfiguration = 0,
    /// <summary>TCP connect failed or the connection dropped during the handshake.</summary>
    Network = 1,
    /// <summary>The HTTP/SOCKS5 proxy refused or failed the tunnel.</summary>
    Proxy = 2,
    Timeout = 3,
    /// <summary>No key stored for this host:port and the origin does not allow prompting (AI/MCP).</summary>
    HostKeyUnknown = 4,
    /// <summary>A different key is stored for this host:port and the origin does not allow prompting (AI/MCP).</summary>
    HostKeyChanged = 5,
    /// <summary>The user rejected the host key, or the key changed between the probe and the real connection.</summary>
    HostKeyRejected = 6,
    AuthenticationFailed = 7,
    /// <summary>The private key is encrypted, no passphrase is stored and prompting is not allowed.</summary>
    PassphraseRequired = 8,
    /// <summary>The user cancelled the passphrase prompt.</summary>
    PassphraseCancelled = 9,
    /// <summary>The passphrase is wrong or the key file is damaged.</summary>
    WrongPassphrase = 10,
    /// <summary>The private key format is not supported or the key is corrupt.</summary>
    InvalidKey = 11,
    /// <summary>The SSH server misbehaved or SSH.NET reported a protocol error.</summary>
    Protocol = 12,
}

/// <summary>A connection attempt failed. <see cref="HopIndex"/> says which hop of the chain (0 = first jump host or the target).</summary>
public sealed class SshConnectException : Exception
{
    public SshConnectException(SshFailureKind kind, string message, int? hopIndex = null, string? hopLabel = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        HopIndex = hopIndex;
        HopLabel = hopLabel;
    }

    public SshConnectException() : this(SshFailureKind.Protocol, "SSH connection failed.") { }

    public SshConnectException(string message) : this(SshFailureKind.Protocol, message) { }

    public SshConnectException(string message, Exception innerException) : this(SshFailureKind.Protocol, message, null, null, innerException) { }

    public SshFailureKind Kind { get; }
    public int? HopIndex { get; }
    public string? HopLabel { get; }
}

/// <summary>An operation on a connection that is no longer usable (lost or closed).</summary>
public sealed class SshConnectionLostException : Exception
{
    public SshConnectionLostException() : base("The SSH connection is no longer available.") { }
    public SshConnectionLostException(string message) : base(message) { }
    public SshConnectionLostException(string message, Exception innerException) : base(message, innerException) { }
}
