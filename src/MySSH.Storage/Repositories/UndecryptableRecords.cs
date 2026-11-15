using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MySSH.Storage;

/// <summary>An encrypted field that could not be decrypted. The owning record is kept; nothing was dropped or overwritten.</summary>
public sealed record UndecryptableRecord(
    string Table,
    Guid RecordId,
    string Field,
    string Reason,
    DateTimeOffset DetectedAt,
    DateTimeOffset? ResolvedAt);

/// <summary>
/// Lists and detects secrets that fail to decrypt (fix for the legacy app silently dropping such servers on load and then
/// erasing them on the next save). Detection happens lazily on access, or eagerly via <see cref="ScanAsync"/>.
/// </summary>
public sealed class UndecryptableRecordStore
{
    private readonly StorageDatabase _db;
    private readonly Vault _vault;

    internal UndecryptableRecordStore(StorageDatabase db, Vault vault)
    {
        _db = db;
        _vault = vault;
    }

    /// <summary>Flagged fields; resolved ones (user saved a new secret) only when <paramref name="includeResolved"/>.</summary>
    public async Task<IReadOnlyList<UndecryptableRecord>> ListAsync(bool includeResolved = false, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = includeResolved
            ? Sql.Command(conn, null, "SELECT table_name, record_id, field, reason, detected_at, resolved_at FROM undecryptable_fields ORDER BY detected_at;")
            : Sql.Command(conn, null, "SELECT table_name, record_id, field, reason, detected_at, resolved_at FROM undecryptable_fields WHERE resolved_at IS NULL ORDER BY detected_at;");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<UndecryptableRecord>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new UndecryptableRecord(r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3),
                Sql.ParseTime(r.GetString(4)), r.IsDBNull(5) ? null : Sql.ParseTime(r.GetString(5))));
        }
        return list;
    }

    /// <summary>Tries to decrypt every stored secret and flags failures. Requires an unlocked vault. Returns the unresolved list.</summary>
    public async Task<IReadOnlyList<UndecryptableRecord>> ScanAsync(CancellationToken ct = default)
    {
        _vault.EnsureUnlocked();
        var blobs = new List<(string Table, Guid Id, string Field, byte[] Blob)>();
        await using (var conn = await _db.OpenAsync(ct).ConfigureAwait(false))
        {
            await using (var cmd = Sql.Command(conn, null, "SELECT id, password, private_key, passphrase FROM identities;"))
            await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = Guid.Parse(r.GetString(0));
                    if (Sql.BlobOrNull(r, 1) is { } a) blobs.Add((Rows.IdentitiesTable, id, Rows.FieldPassword, a));
                    if (Sql.BlobOrNull(r, 2) is { } b) blobs.Add((Rows.IdentitiesTable, id, Rows.FieldPrivateKey, b));
                    if (Sql.BlobOrNull(r, 3) is { } c) blobs.Add((Rows.IdentitiesTable, id, Rows.FieldPassphrase, c));
                }
            }
            await using (var cmd = Sql.Command(conn, null, "SELECT id, password FROM proxies WHERE password IS NOT NULL;"))
            await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    blobs.Add((Rows.ProxiesTable, Guid.Parse(r.GetString(0)), Rows.FieldPassword, (byte[])r.GetValue(1)));
            }

            foreach (var (table, id, field, blob) in blobs)
            {
                try
                {
                    _vault.Decrypt(table, id, field, blob);
                }
                catch (CryptographicException e)
                {
                    await Undecryptable.FlagAsync(conn, null, table, id, field, e.Message, blob, ct).ConfigureAwait(false);
                }
            }
        }
        return await ListAsync(false, ct).ConfigureAwait(false);
    }

    /// <summary>Remove a flag the user acknowledged. The original record is not touched.</summary>
    public async Task DismissAsync(string table, Guid recordId, string field, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await Sql.ExecAsync(conn, null, "DELETE FROM undecryptable_fields WHERE table_name = $t AND record_id = $id AND field = $f;", ct,
            ("$t", table), ("$id", Sql.Id(recordId)), ("$f", field)).ConfigureAwait(false);
    }
}

internal static class Undecryptable
{
    public static Task FlagAsync(SqliteConnection conn, SqliteTransaction? tx, string table, Guid id, string field, string reason, byte[] blob, CancellationToken ct) =>
        Sql.ExecAsync(conn, tx, """
            INSERT INTO undecryptable_fields (table_name, record_id, field, reason, raw_value, detected_at, resolved_at)
            VALUES ($t, $id, $f, $reason, $raw, $now, NULL)
            ON CONFLICT(table_name, record_id, field) DO UPDATE SET reason = excluded.reason, raw_value = excluded.raw_value,
                detected_at = CASE WHEN undecryptable_fields.resolved_at IS NULL THEN undecryptable_fields.detected_at ELSE excluded.detected_at END,
                resolved_at = NULL;
            """, ct, ("$t", table), ("$id", Sql.Id(id)), ("$f", field), ("$reason", reason), ("$raw", blob), ("$now", Sql.Time(DateTimeOffset.UtcNow)));

    /// <summary>The record's secrets were replaced by the user: mark flags resolved (the raw copy of the old blob is kept).</summary>
    public static Task ResolveAsync(SqliteConnection conn, SqliteTransaction? tx, string table, Guid id, CancellationToken ct) =>
        Sql.ExecAsync(conn, tx, "UPDATE undecryptable_fields SET resolved_at = $now WHERE table_name = $t AND record_id = $id AND resolved_at IS NULL;", ct,
            ("$now", Sql.Time(DateTimeOffset.UtcNow)), ("$t", table), ("$id", Sql.Id(id)));

    public static Task ForgetAsync(SqliteConnection conn, SqliteTransaction? tx, string table, Guid id, CancellationToken ct) =>
        Sql.ExecAsync(conn, tx, "DELETE FROM undecryptable_fields WHERE table_name = $t AND record_id = $id;", ct, ("$t", table), ("$id", Sql.Id(id)));
}
