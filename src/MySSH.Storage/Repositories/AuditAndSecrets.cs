using MySSH.Core;

namespace MySSH.Storage;

/// <summary>Table <c>audit_log</c> (design §3 AuditEntry). Append-only; host_id is not a foreign key so entries outlive hosts.</summary>
internal sealed class AuditLogStore(StorageDatabase db) : IAuditLogStore
{
    /// <summary>Upper bound for <see cref="AuditQuery.Limit"/>.</summary>
    public const int MaxLimit = 10_000;

    public async Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(entry.Actor, nameof(entry));
        ArgumentException.ThrowIfNullOrEmpty(entry.Tool, nameof(entry));
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await Sql.ExecAsync(conn, null, """
            INSERT INTO audit_log (id, ts, actor, tool, host_id, args_json, decision, exit_code, duration_ms, error)
            VALUES ($id, $ts, $actor, $tool, $host, $args, $decision, $exit, $duration, $error);
            """, ct,
            ("$id", Sql.Id(entry.Id)), ("$ts", entry.Timestamp.UtcTicks), ("$actor", entry.Actor), ("$tool", entry.Tool),
            ("$host", Sql.Id(entry.HostId)), ("$args", entry.ArgumentsJson ?? "{}"), ("$decision", (int)entry.Decision),
            ("$exit", entry.ExitCode), ("$duration", entry.DurationMs), ("$error", entry.Error)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 0, MaxLimit);
        var list = new List<AuditEntry>();
        if (limit == 0)
            return list;
        // One constant statement; absent filters are neutralized with "$x IS NULL OR ...".
        const string sql = """
            SELECT id, ts, actor, tool, host_id, args_json, decision, exit_code, duration_ms, error
            FROM audit_log
            WHERE ($before IS NULL OR ts < $before)
              AND ($host IS NULL OR host_id = $host)
              AND ($prefix IS NULL OR substr(actor, 1, length($prefix)) = $prefix)
            ORDER BY ts DESC, rowid DESC
            LIMIT $limit;
            """;
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Sql.Command(conn, null, sql,
            ("$before", query.Before?.UtcTicks), ("$host", Sql.Id(query.HostId)),
            ("$prefix", string.IsNullOrEmpty(query.ActorPrefix) ? null : query.ActorPrefix), ("$limit", limit));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new AuditEntry(
                Guid.Parse(r.GetString(0)),
                new DateTimeOffset(r.GetInt64(1), TimeSpan.Zero),
                r.GetString(2),
                r.GetString(3),
                Sql.GuidOrNull(r, 4),
                r.GetString(5),
                (AuditDecision)r.GetInt32(6),
                r.IsDBNull(7) ? null : r.GetInt32(7),
                r.GetInt64(8),
                Sql.StringOrNull(r, 9)));
        }
        return list;
    }

    public async Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        return await Sql.ExecAsync(conn, null, "DELETE FROM audit_log WHERE ts < $cutoff;", ct, ("$cutoff", cutoff.UtcTicks)).ConfigureAwait(false);
    }
}

/// <summary>
/// Table <c>secret_settings</c>: named secrets encrypted with the vault master key (AES-256-GCM, AAD
/// <c>"secret_settings:{id}:value"</c>, id = name-based UUID of the key). Not part of backups.
/// </summary>
internal sealed class SecretSettingsStore(StorageDatabase db, Vault vault) : ISecretSettingsStore
{
    public const string Table = "secret_settings";
    public const string FieldValue = "value";

    internal static Guid RecordId(string key) => DeterministicGuid.Create("secret-setting:" + key);

    /// <exception cref="VaultLockedException"/>
    /// <exception cref="SecretUnavailableException">The stored value failed to decrypt; it is flagged and kept.</exception>
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        vault.EnsureUnlocked();
        byte[]? blob;
        await using (var conn = await db.OpenAsync(ct).ConfigureAwait(false))
            blob = (byte[]?)await Sql.ScalarAsync(conn, null, "SELECT value FROM secret_settings WHERE key = $k;", ct, ("$k", key)).ConfigureAwait(false);
        return await Rows.DecryptOrFlagAsync(db, vault, Table, RecordId(key), FieldValue, blob, ct).ConfigureAwait(false);
    }

    /// <exception cref="VaultLockedException"/>
    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        vault.EnsureUnlocked();
        var id = RecordId(key);
        var blob = vault.Encrypt(Table, id, FieldValue, value);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        await Sql.ExecAsync(conn, tx, """
            INSERT INTO secret_settings (key, id, value, updated_at) VALUES ($k, $id, $v, $t)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """, ct, ("$k", key), ("$id", Sql.Id(id)), ("$v", blob), ("$t", Sql.Time(DateTimeOffset.UtcNow))).ConfigureAwait(false);
        await Undecryptable.ResolveAsync(conn, tx, Table, id, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        var removed = await Sql.ExecAsync(conn, tx, "DELETE FROM secret_settings WHERE key = $k;", ct, ("$k", key)).ConfigureAwait(false) > 0;
        await Undecryptable.ForgetAsync(conn, tx, Table, RecordId(key), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return removed;
    }
}
