namespace MySSH.Core;

/// <summary>Outcome of a tool call as recorded in the audit log (design §3 AuditEntry, §6.1).</summary>
public enum AuditDecision
{
    /// <summary>Allowed by policy without asking.</summary>
    Allowed = 0,
    /// <summary>The user approved it.</summary>
    Approved = 1,
    /// <summary>The user rejected it.</summary>
    Denied = 2,
    /// <summary>Nobody answered the approval prompt in time (counts as denied).</summary>
    TimedOut = 3,
    /// <summary>The caller cancelled while waiting for approval or during execution.</summary>
    Cancelled = 4,
    /// <summary>Rejected by the host policy (e.g. not on the ReadOnly allowlist, writes under ReadOnly).</summary>
    PolicyRejected = 5,
    /// <summary>Arguments failed schema validation or named an unknown / hidden host or session.</summary>
    Invalid = 6,
}

/// <param name="Actor">"user", "mcp:&lt;client&gt;" or "ai:&lt;provider&gt;/&lt;model&gt;".</param>
/// <param name="ArgumentsJson">Tool arguments as JSON, with file contents / terminal text replaced by a length marker.</param>
/// <param name="ExitCode">Remote exit status for run_command.</param>
/// <param name="Error">Execution error when the call was allowed but failed (connection, SFTP, ...).</param>
public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    string Actor,
    string Tool,
    Guid? HostId,
    string ArgumentsJson,
    AuditDecision Decision,
    int? ExitCode,
    long DurationMs,
    string? Error);

public sealed record AuditQuery
{
    public int Limit { get; init; } = 200;
    /// <summary>Entries strictly older than this (paging); null = newest.</summary>
    public DateTimeOffset? Before { get; init; }
    public Guid? HostId { get; init; }
    /// <summary>Prefix match on Actor, e.g. "mcp:" or "ai:".</summary>
    public string? ActorPrefix { get; init; }
}

/// <summary>Append-only audit log. Implemented by MySSH.Storage (table audit_log).</summary>
public interface IAuditLogStore
{
    Task AppendAsync(AuditEntry entry, CancellationToken ct = default);
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default);
    /// <summary>Deletes entries older than <paramref name="cutoff"/>; returns the number removed.</summary>
    Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
