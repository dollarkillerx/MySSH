namespace MySSH.Core;

/// <summary>Who initiated a connection. AI/MCP connections never prompt and use strict host-key checking (design §5.2, §6).</summary>
public enum ConnectionOrigin { User = 0, Ai = 1, Mcp = 2 }

/// <summary>
/// A byte-stream terminal backend (SSH ShellStream, local PTY in tests) consumed by the terminal control.
/// Implemented in MySSH.Ssh; consumed by MySSH.Terminal. The backend must keep reading even when nobody is
/// listening: SSH.NET has no back-pressure (design §8), buffering limits belong to the terminal side.
/// </summary>
public interface ITerminalBackend : IAsyncDisposable
{
    /// <summary>Raised on a background thread. The memory is only valid during the callback.</summary>
    event Action<ReadOnlyMemory<byte>>? DataReceived;

    /// <summary>Raised once when the remote shell ends or the connection is lost.</summary>
    event Action<TerminalExit>? Exited;

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Window-change request. Safe to call from the UI thread; must not block.</summary>
    void Resize(int columns, int rows);
}

/// <param name="ExitCode">Remote exit status when known.</param>
/// <param name="ConnectionLost">True when the transport dropped (offer reconnect), false for a normal shell exit.</param>
public sealed record TerminalExit(int? ExitCode, bool ConnectionLost, string? Reason = null);

public enum HostKeyStatus
{
    /// <summary>No key stored for this host:port yet (trust on first use).</summary>
    Unknown = 0,
    /// <summary>A different key is stored for this host:port: possible man-in-the-middle.</summary>
    Changed = 1,
}

/// <param name="HostPort">The REAL host:port in known_hosts format (never a forwarded loopback port).</param>
/// <param name="Fingerprint">OpenSSH format, "SHA256:…".</param>
/// <param name="PreviousFingerprint">Set when <paramref name="Status"/> is Changed.</param>
/// <param name="HopIndex">0 = first hop of a jump chain; equals HopCount-1 for the target.</param>
public sealed record HostKeyCheck(
    string HostPort, string KeyType, string Fingerprint, string PublicKeyBase64, HostKeyStatus Status,
    string? PreviousFingerprint, string HostLabel, int HopIndex, int HopCount);

public enum HostKeyDecision { Reject = 0, AcceptOnce = 1, AcceptAndSave = 2 }

/// <summary>UI hook for host keys that are unknown or changed. Only called for <see cref="ConnectionOrigin.User"/>.</summary>
public interface IHostKeyVerifier
{
    ValueTask<HostKeyDecision> VerifyAsync(HostKeyCheck check, CancellationToken ct);
}

/// <summary>UI hook for secrets that are not stored (e.g. an encrypted private key saved without its passphrase).</summary>
public interface ICredentialPrompt
{
    /// <returns>null = user cancelled.</returns>
    ValueTask<string?> PromptPassphraseAsync(Identity identity, string? previousAttemptError, CancellationToken ct);
}
