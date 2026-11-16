using System.Security.Cryptography;
using MySSH.Core;

namespace MySSH.Ssh;

public static class HostKeyFingerprint
{
    /// <summary>OpenSSH format: "SHA256:" + unpadded base64 of SHA-256 over the raw public key blob (same as <c>ssh-keygen -lf</c>).</summary>
    public static string Sha256(ReadOnlySpan<byte> publicKeyBlob)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(publicKeyBlob, hash);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }
}

internal enum HostKeyMatch { Match, Unknown, Changed }

internal sealed record HostKeyEvaluation(HostKeyMatch Match, string? PreviousFingerprint);

/// <summary>What the server presented during a handshake.</summary>
internal sealed record ObservedHostKey(string KeyType, byte[] Blob, HostKeyEvaluation Evaluation)
{
    public string Fingerprint => HostKeyFingerprint.Sha256(Blob);
    public string Base64 => Convert.ToBase64String(Blob);
}

internal static class HostKeyPolicy
{
    /// <summary>
    /// Compares a presented key with the trusted keys of that host:port. A known host presenting a key of a type we have no
    /// entry for counts as Changed (the connector puts stored key types first, so an honest server presents a stored type).
    /// </summary>
    public static HostKeyEvaluation Evaluate(IReadOnlyList<KnownHostEntry> stored, string keyType, ReadOnlySpan<byte> blob)
    {
        if (stored.Count == 0) return new(HostKeyMatch.Unknown, null);
        var fp = HostKeyFingerprint.Sha256(blob);
        var b64 = Convert.ToBase64String(blob);
        KnownHostEntry? sameType = null;
        foreach (var e in stored)
        {
            if (!string.Equals(e.KeyType, keyType, StringComparison.Ordinal)) continue;
            sameType = e;
            bool match = e.PublicKeyBase64 is { Length: > 0 } pk
                ? string.Equals(pk, b64, StringComparison.Ordinal)
                : string.Equals(e.Sha256Fingerprint, fp, StringComparison.Ordinal);
            if (match) return new(HostKeyMatch.Match, null);
        }
        return new(HostKeyMatch.Changed, (sameType ?? stored[0]).Sha256Fingerprint);
    }

    public static bool IsStrict(ConnectionOrigin origin) => origin != ConnectionOrigin.User;

    public static KnownHostEntry ToEntry(string hostPort, ObservedHostKey key, DateTimeOffset now) => new()
    {
        HostPort = hostPort,
        KeyType = key.KeyType,
        Sha256Fingerprint = key.Fingerprint,
        PublicKeyBase64 = key.Base64,
        FirstSeen = now,
        LastSeen = now,
    };

    /// <summary>
    /// Decides what to do with an unknown/changed key (two-phase TOFU, phase 2). Strict origins fail; user origins ask the
    /// verifier and persist on AcceptAndSave. Returns true when the key was accepted only for this connection.
    /// </summary>
    public static async Task<bool> ResolveAsync(
        ObservedHostKey key, string hostPort, string hostLabel, int hopIndex, int hopCount,
        ConnectionOrigin origin, IHostKeyVerifier verifier, IKnownHostStore store, CancellationToken ct)
    {
        var eval = key.Evaluation;
        if (eval.Match == HostKeyMatch.Match) return false;
        if (IsStrict(origin))
        {
            throw eval.Match == HostKeyMatch.Unknown
                ? new SshConnectException(SshFailureKind.HostKeyUnknown,
                    $"Host key of {hostPort} ({key.Fingerprint}) is not trusted yet. {origin} connections never accept unknown host keys; connect once interactively to trust it.", hopIndex, hostLabel)
                : new SshConnectException(SshFailureKind.HostKeyChanged,
                    $"Host key of {hostPort} has CHANGED (was {eval.PreviousFingerprint}, now {key.Fingerprint}). {origin} connection refused.", hopIndex, hostLabel);
        }

        var check = new HostKeyCheck(hostPort, key.KeyType, key.Fingerprint, key.Base64,
            eval.Match == HostKeyMatch.Unknown ? HostKeyStatus.Unknown : HostKeyStatus.Changed,
            eval.PreviousFingerprint, hostLabel, hopIndex, hopCount);
        var decision = await verifier.VerifyAsync(check, ct);
        switch (decision)
        {
            case HostKeyDecision.AcceptAndSave:
                await store.UpsertAsync(ToEntry(hostPort, key, DateTimeOffset.UtcNow), ct);
                return false;
            case HostKeyDecision.AcceptOnce:
                return true;
            default:
                throw new SshConnectException(SshFailureKind.HostKeyRejected, $"Host key of {hostPort} ({key.Fingerprint}) was rejected.", hopIndex, hostLabel);
        }
    }
}

/// <summary>
/// Synchronous host-key check run inside SSH.NET's <c>HostKeyReceived</c> (an SSH.NET thread). Never blocks: it only compares
/// against keys fetched before the handshake, or against the key pinned for this chain, and records what it saw.
/// </summary>
internal sealed class HostKeyGate(IReadOnlyList<KnownHostEntry> stored, ObservedHostKey? pinned)
{
    public ObservedHostKey? Observed { get; private set; }

    public bool Check(string keyType, byte[] blob)
    {
        HostKeyEvaluation eval;
        if (pinned is not null)
        {
            bool same = string.Equals(pinned.KeyType, keyType, StringComparison.Ordinal) && pinned.Blob.AsSpan().SequenceEqual(blob);
            eval = same ? new(HostKeyMatch.Match, null) : new(HostKeyMatch.Changed, pinned.Fingerprint);
        }
        else
        {
            eval = HostKeyPolicy.Evaluate(stored, keyType, blob);
        }
        Observed = new ObservedHostKey(keyType, (byte[])blob.Clone(), eval);
        return eval.Match == HostKeyMatch.Match;
    }
}
