using MySSH.Core;

namespace MySSH.Storage;

public enum ImportSource { LegacyAppData, LegacyBackupV1, BackupV2 }

public enum ImportIssueSeverity { Warning, Error }

/// <summary>A problem with one source record. <see cref="ImportIssueSeverity.Error"/> means the record was skipped.</summary>
public sealed record ImportIssue(string RecordId, string? RecordName, ImportIssueSeverity Severity, string Reason);

/// <summary>Per-entity counts. <see cref="Skipped"/> = already present and left as is (idempotent re-run).</summary>
public sealed record EntityImportCounts(int Added, int Updated, int Skipped)
{
    public static EntityImportCounts None { get; } = new(0, 0, 0);
    public int Total => Added + Updated + Skipped;
}

public sealed class ImportResult
{
    public ImportSource Source { get; init; }
    public EntityImportCounts Hosts { get; init; } = EntityImportCounts.None;
    public EntityImportCounts Identities { get; init; } = EntityImportCounts.None;
    public EntityImportCounts Proxies { get; init; } = EntityImportCounts.None;
    public EntityImportCounts Groups { get; init; } = EntityImportCounts.None;
    public EntityImportCounts KnownHosts { get; init; } = EntityImportCounts.None;
    public EntityImportCounts Settings { get; init; } = EntityImportCounts.None;
    /// <summary>Source records that could not be imported (each has an Error issue).</summary>
    public int FailedRecords { get; init; }
    public IReadOnlyList<ImportIssue> Issues { get; init; } = [];
    /// <summary>
    /// True for the legacy v1 backup format (password → single SHA-256, no KDF). The UI should recommend creating a new
    /// v2 backup and deleting the old file.
    /// </summary>
    public bool IsWeakLegacyFormat { get; init; }
}

/// <summary>Entities ready to be written by <see cref="ImportWriter"/>.</summary>
internal sealed class ImportBatch
{
    public List<HostGroup> Groups { get; } = [];
    public List<(Identity Identity, IdentitySecrets? Secrets)> Identities { get; } = [];
    public List<(ProxyProfile Proxy, string? Password)> Proxies { get; } = [];
    public List<Host> Hosts { get; } = [];
    public List<KnownHostEntry> KnownHosts { get; } = [];
    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);
    public List<ImportIssue> Issues { get; } = [];
    public int FailedRecords { get; set; }

    public void Warn(string id, string? name, string reason) => Issues.Add(new ImportIssue(id, name, ImportIssueSeverity.Warning, reason));

    public void Fail(string id, string? name, string reason)
    {
        Issues.Add(new ImportIssue(id, name, ImportIssueSeverity.Error, reason));
        FailedRecords++;
    }
}

internal enum ImportMode
{
    /// <summary>Records whose id already exists are left untouched (legacy import: idempotent, never clobbers user edits).</summary>
    SkipExisting,
    /// <summary>Records are upserted by id (backup restore = merge by id).</summary>
    Overwrite,
}
