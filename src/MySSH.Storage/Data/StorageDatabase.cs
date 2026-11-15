using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MySSH.Storage;

/// <summary>Thrown when the database was written by a newer MySSH (schema migrations are forward-only).</summary>
public sealed class StorageSchemaTooNewException : Exception
{
    public StorageSchemaTooNewException() { }
    public StorageSchemaTooNewException(string message) : base(message) { }
    public StorageSchemaTooNewException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>SQLite access: pooled connections (foreign keys on), WAL, forward-only migrations.</summary>
internal sealed class StorageDatabase
{
    private readonly string _connectionString;

    public StorageDatabase(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string Path { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void ClearPool()
    {
        using var conn = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(conn);
    }

    /// <summary>Creates the file if needed, enables WAL and applies pending migrations. Returns the schema version.</summary>
    public async Task<int> InitializeAsync(CancellationToken ct)
    {
        var isNew = !File.Exists(Path);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        if (isNew)
            FileUtil.RestrictToOwner(Path);
        await Sql.ExecAsync(conn, null, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await Sql.ExecAsync(conn, null, "PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
        await Sql.ExecAsync(conn, null,
            "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL);", ct).ConfigureAwait(false);

        var current = await CurrentVersionAsync(conn, ct).ConfigureAwait(false);
        var latest = Migrations.All[^1].Version;
        if (current > latest)
            throw new StorageSchemaTooNewException(
                $"The database '{Path}' has schema version {current}, but this MySSH only knows up to {latest}. Update MySSH.");

        foreach (var m in Migrations.All)
        {
            if (m.Version <= current) continue;
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await Sql.ExecAsync(conn, tx, m.Sql, ct).ConfigureAwait(false);
            await Sql.ExecAsync(conn, tx, "INSERT INTO schema_version (version, applied_at) VALUES ($v, $t);", ct,
                ("$v", m.Version), ("$t", Sql.Time(DateTimeOffset.UtcNow))).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            current = m.Version;
        }
        return current;
    }

    public static async Task<int> CurrentVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        var v = await Sql.ScalarAsync(conn, null, "SELECT COALESCE(MAX(version), 0) FROM schema_version;", ct).ConfigureAwait(false);
        return Convert.ToInt32(v, CultureInfo.InvariantCulture);
    }
}

internal sealed record Migration(int Version, string Sql);

internal static class Migrations
{
    /// <summary>Append-only. Never edit a shipped migration; add a new one.</summary>
    public static readonly Migration[] All =
    [
        new(1, """
            CREATE TABLE groups (
                id          TEXT    NOT NULL PRIMARY KEY,
                name        TEXT    NOT NULL,
                parent_id   TEXT    NULL REFERENCES groups(id) ON DELETE SET NULL DEFERRABLE INITIALLY DEFERRED,
                sort        INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE identities (
                id          TEXT    NOT NULL PRIMARY KEY,
                label       TEXT    NOT NULL,
                username    TEXT    NULL,
                kind        INTEGER NOT NULL,
                password    BLOB    NULL,   -- AES-GCM field blob, AAD "identities:{id}:password"
                private_key BLOB    NULL,   -- AAD "identities:{id}:private_key"
                passphrase  BLOB    NULL,   -- AAD "identities:{id}:passphrase"
                created_at  TEXT    NOT NULL,
                updated_at  TEXT    NOT NULL
            );

            CREATE TABLE proxies (
                id          TEXT    NOT NULL PRIMARY KEY,
                label       TEXT    NOT NULL,
                kind        INTEGER NOT NULL,
                hostname    TEXT    NOT NULL,
                port        INTEGER NOT NULL,
                username    TEXT    NULL,
                password    BLOB    NULL    -- AAD "proxies:{id}:password"
            );

            CREATE TABLE hosts (
                id                  TEXT    NOT NULL PRIMARY KEY,
                label               TEXT    NOT NULL,
                hostname            TEXT    NOT NULL,
                port                INTEGER NOT NULL,
                username            TEXT    NULL,
                identity_id         TEXT    NULL REFERENCES identities(id) DEFERRABLE INITIALLY DEFERRED,
                group_id            TEXT    NULL REFERENCES groups(id) ON DELETE SET NULL DEFERRABLE INITIALLY DEFERRED,
                tags                TEXT    NOT NULL DEFAULT '[]' CHECK (json_valid(tags)),
                proxy_id            TEXT    NULL REFERENCES proxies(id) DEFERRABLE INITIALLY DEFERRED,
                keep_alive_seconds  INTEGER NOT NULL DEFAULT 30,
                encoding            TEXT    NOT NULL DEFAULT 'utf-8',
                ai_policy           INTEGER NOT NULL DEFAULT 0,
                ai_identity_id      TEXT    NULL REFERENCES identities(id) DEFERRABLE INITIALLY DEFERRED,
                notes               TEXT    NULL,
                created_at          TEXT    NOT NULL,
                updated_at          TEXT    NOT NULL
            );
            CREATE INDEX ix_hosts_identity ON hosts(identity_id);
            CREATE INDEX ix_hosts_ai_identity ON hosts(ai_identity_id);
            CREATE INDEX ix_hosts_proxy ON hosts(proxy_id);
            CREATE INDEX ix_hosts_group ON hosts(group_id);

            -- Jump chain as a join table so the database itself refuses dangling references.
            CREATE TABLE host_jumps (
                host_id         TEXT    NOT NULL REFERENCES hosts(id) ON DELETE CASCADE,
                position        INTEGER NOT NULL,
                jump_host_id    TEXT    NOT NULL REFERENCES hosts(id) DEFERRABLE INITIALLY DEFERRED,
                PRIMARY KEY (host_id, position)
            ) WITHOUT ROWID;
            CREATE INDEX ix_host_jumps_jump ON host_jumps(jump_host_id);

            CREATE TABLE known_hosts (
                host_port           TEXT NOT NULL,
                key_type            TEXT NOT NULL,
                sha256_fingerprint  TEXT NOT NULL,
                public_key          TEXT NULL,
                first_seen          TEXT NOT NULL,
                last_seen           TEXT NOT NULL,
                PRIMARY KEY (host_port, key_type)
            ) WITHOUT ROWID;

            CREATE TABLE settings (
                key         TEXT NOT NULL PRIMARY KEY,
                value       TEXT NOT NULL CHECK (json_valid(value)),
                updated_at  TEXT NOT NULL
            ) WITHOUT ROWID;

            -- Secrets that failed to decrypt. The original row is never modified because of a failure; a copy of the
            -- undecryptable blob is kept here even if the user later replaces the secret.
            CREATE TABLE undecryptable_fields (
                table_name  TEXT NOT NULL,
                record_id   TEXT NOT NULL,
                field       TEXT NOT NULL,
                reason      TEXT NOT NULL,
                raw_value   BLOB NULL,
                detected_at TEXT NOT NULL,
                resolved_at TEXT NULL,
                PRIMARY KEY (table_name, record_id, field)
            ) WITHOUT ROWID;
            """),
    ];
}

/// <summary>Small ADO.NET helpers. All SQL passed in is constant; values always go through parameters.</summary>
internal static class Sql
{
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Callers pass constant SQL; values are parameterized.")]
    public static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static async Task<int> ExecAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(conn, tx, sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<object?> ScalarAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(conn, tx, sql, parameters);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is DBNull ? null : v;
    }

    public static async Task<bool> ExistsAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters) =>
        await ScalarAsync(conn, tx, sql, ct, parameters).ConfigureAwait(false) is not null;

    public static async Task<List<Guid>> GuidsAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(conn, tx, sql, parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Guid>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(Guid.Parse(r.GetString(0)));
        return list;
    }

    public static async Task<SqliteTransaction> BeginAsync(SqliteConnection conn, CancellationToken ct) =>
        (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

    public static string Id(Guid id) => id.ToString("D");
    public static string? Id(Guid? id) => id?.ToString("D");
    public static string Time(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);
    public static DateTimeOffset ParseTime(string s) => DateTimeOffset.ParseExact(s, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static string? StringOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static Guid? GuidOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));
    public static byte[]? BlobOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : (byte[])r.GetValue(i);
}
