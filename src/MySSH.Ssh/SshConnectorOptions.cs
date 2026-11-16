namespace MySSH.Ssh;

public enum HopTransportKind
{
    /// <summary>SSH.NET <c>ForwardedPortLocal(127.0.0.1, 0)</c> kept alive for the chain lifetime; fail-closed on a second local connection.</summary>
    ForwardedPort = 0,
    /// <summary>
    /// Own listener that accepts exactly one connection and pumps it through an SSH.NET-internal direct-tcpip channel
    /// (<see cref="System.Runtime.CompilerServices.UnsafeAccessorAttribute"/>, NativeAOT-safe). Falls back to
    /// <see cref="ForwardedPort"/> when the startup self-check fails (e.g. after an SSH.NET upgrade).
    /// </summary>
    SingleAcceptBridge = 1,
}

/// <summary>Timeouts and liveness settings of <see cref="SshConnector"/>.</summary>
public sealed record SshConnectorOptions
{
    /// <summary>Per-hop TCP + handshake + authentication timeout (SSH.NET <c>ConnectionInfo.Timeout</c> while connecting).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary><c>ConnectionInfo.Timeout</c> after connecting (channel open, requests) and the SFTP operation timeout.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Overrides <see cref="Core.Host.KeepAliveSeconds"/> when set. SSH.NET keepalives are SSH_MSG_IGNORE: they keep NAT
    /// mappings alive but cannot detect a frozen peer (see <see cref="HeartbeatInterval"/>).
    /// </summary>
    public TimeSpan? KeepAliveOverride { get; init; }

    /// <summary>Idle interval after which the app-level heartbeat probes the target. Zero or negative disables it.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A heartbeat probe that does not answer within this time marks the connection Lost.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public HopTransportKind HopTransport { get; init; } = HopTransportKind.ForwardedPort;

    /// <summary>Custom hop transport (tests, future implementations). Takes precedence over <see cref="HopTransport"/>.</summary>
    public IHopTransport? CustomHopTransport { get; init; }

    /// <summary>How often the user may retry a passphrase before the connect fails.</summary>
    public int MaxPassphraseAttempts { get; init; } = 3;

    /// <summary>Optional SSH.NET logging (never logs secrets).</summary>
    public Microsoft.Extensions.Logging.ILoggerFactory? LoggerFactory { get; init; }
}
