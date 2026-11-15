using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MySSH.Core;

namespace MySSH.Storage;

/// <summary>Row-level SQL shared by the repositories, the import writer and backups. Callers own the transaction.</summary>
internal static class Rows
{
    public const string HostsTable = "hosts";
    public const string IdentitiesTable = "identities";
    public const string ProxiesTable = "proxies";

    public const string FieldPassword = "password";
    public const string FieldPrivateKey = "private_key";
    public const string FieldPassphrase = "passphrase";

    // ---------------- hosts ----------------

    private const string HostColumns =
        "id, label, hostname, port, username, identity_id, group_id, tags, proxy_id, keep_alive_seconds, encoding, ai_policy, ai_identity_id, notes, created_at, updated_at";

    public static async Task<List<Host>> ReadHostsAsync(SqliteConnection conn, SqliteTransaction? tx, Guid? onlyId, CancellationToken ct)
    {
        var jumps = new Dictionary<Guid, List<Guid>>();
        await using (var jc = onlyId is { } jid
            ? Sql.Command(conn, tx, "SELECT host_id, jump_host_id FROM host_jumps WHERE host_id = $id ORDER BY position;", ("$id", Sql.Id(jid)))
            : Sql.Command(conn, tx, "SELECT host_id, jump_host_id FROM host_jumps ORDER BY host_id, position;"))
        await using (var r = await jc.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var hid = Guid.Parse(r.GetString(0));
                if (!jumps.TryGetValue(hid, out var list))
                    jumps[hid] = list = [];
                list.Add(Guid.Parse(r.GetString(1)));
            }
        }

        var hosts = new List<Host>();
        await using var cmd = onlyId is { } id
            ? Sql.Command(conn, tx, "SELECT " + HostColumns + " FROM hosts WHERE id = $id;", ("$id", Sql.Id(id)))
            : Sql.Command(conn, tx, "SELECT " + HostColumns + " FROM hosts ORDER BY label COLLATE NOCASE, id;");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var hid = Guid.Parse(reader.GetString(0));
            hosts.Add(new Host
            {
                Id = hid,
                Label = reader.GetString(1),
                Hostname = reader.GetString(2),
                Port = reader.GetInt32(3),
                Username = Sql.StringOrNull(reader, 4),
                IdentityId = Sql.GuidOrNull(reader, 5),
                GroupId = Sql.GuidOrNull(reader, 6),
                Tags = JsonSerializer.Deserialize(reader.GetString(7), StorageJsonContext.Default.ListString) ?? [],
                ProxyId = Sql.GuidOrNull(reader, 8),
                KeepAliveSeconds = reader.GetInt32(9),
                Encoding = reader.GetString(10),
                AiPolicy = (AiPolicy)reader.GetInt32(11),
                AiIdentityId = Sql.GuidOrNull(reader, 12),
                Notes = Sql.StringOrNull(reader, 13),
                CreatedAt = Sql.ParseTime(reader.GetString(14)),
                UpdatedAt = Sql.ParseTime(reader.GetString(15)),
                JumpChain = jumps.TryGetValue(hid, out var chain) ? chain : [],
            });
        }
        return hosts;
    }

    /// <summary>Validates references, then inserts or updates the host and replaces its jump chain.</summary>
    public static async Task UpsertHostAsync(SqliteConnection conn, SqliteTransaction tx, Host host, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host.Label, nameof(host));
        ArgumentException.ThrowIfNullOrWhiteSpace(host.Hostname, nameof(host));
        if (host.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(host), host.Port, "Port must be between 1 and 65535.");
        if (host.JumpChain.Contains(host.Id))
            throw new ArgumentException("A host cannot be its own jump host.", nameof(host));

        await RequireAsync(conn, tx, "SELECT 1 FROM identities WHERE id = $id;", host.IdentityId, "identity", ct).ConfigureAwait(false);
        await RequireAsync(conn, tx, "SELECT 1 FROM identities WHERE id = $id;", host.AiIdentityId, "AI identity", ct).ConfigureAwait(false);
        await RequireAsync(conn, tx, "SELECT 1 FROM proxies WHERE id = $id;", host.ProxyId, "proxy", ct).ConfigureAwait(false);
        await RequireAsync(conn, tx, "SELECT 1 FROM groups WHERE id = $id;", host.GroupId, "group", ct).ConfigureAwait(false);

        await Sql.ExecAsync(conn, tx, """
            INSERT INTO hosts (id, label, hostname, port, username, identity_id, group_id, tags, proxy_id, keep_alive_seconds,
                               encoding, ai_policy, ai_identity_id, notes, created_at, updated_at)
            VALUES ($id, $label, $hostname, $port, $username, $identity, $group, $tags, $proxy, $keepalive,
                    $encoding, $ai, $aiidentity, $notes, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                label = excluded.label, hostname = excluded.hostname, port = excluded.port, username = excluded.username,
                identity_id = excluded.identity_id, group_id = excluded.group_id, tags = excluded.tags,
                proxy_id = excluded.proxy_id, keep_alive_seconds = excluded.keep_alive_seconds, encoding = excluded.encoding,
                ai_policy = excluded.ai_policy, ai_identity_id = excluded.ai_identity_id, notes = excluded.notes,
                created_at = excluded.created_at, updated_at = excluded.updated_at;
            """, ct,
            ("$id", Sql.Id(host.Id)), ("$label", host.Label), ("$hostname", host.Hostname), ("$port", host.Port),
            ("$username", host.Username), ("$identity", Sql.Id(host.IdentityId)), ("$group", Sql.Id(host.GroupId)),
            ("$tags", JsonSerializer.Serialize(host.Tags.ToList(), StorageJsonContext.Default.ListString)),
            ("$proxy", Sql.Id(host.ProxyId)), ("$keepalive", host.KeepAliveSeconds), ("$encoding", host.Encoding),
            ("$ai", (int)host.AiPolicy), ("$aiidentity", Sql.Id(host.AiIdentityId)), ("$notes", host.Notes),
            ("$created", Sql.Time(host.CreatedAt)), ("$updated", Sql.Time(host.UpdatedAt))).ConfigureAwait(false);

        await Sql.ExecAsync(conn, tx, "DELETE FROM host_jumps WHERE host_id = $id;", ct, ("$id", Sql.Id(host.Id))).ConfigureAwait(false);
        for (var i = 0; i < host.JumpChain.Count; i++)
        {
            await Sql.ExecAsync(conn, tx, "INSERT INTO host_jumps (host_id, position, jump_host_id) VALUES ($h, $p, $j);", ct,
                ("$h", Sql.Id(host.Id)), ("$p", i), ("$j", Sql.Id(host.JumpChain[i]))).ConfigureAwait(false);
        }
    }

    /// <summary>Jump hosts must exist by commit time (FK is deferred so a batch can insert hosts in any order).</summary>
    public static async Task RequireJumpHostsAsync(SqliteConnection conn, SqliteTransaction tx, Host host, CancellationToken ct)
    {
        if (host.JumpChain.Contains(host.Id))
            throw new ArgumentException("A host cannot be its own jump host.", nameof(host));
        foreach (var j in host.JumpChain)
            await RequireAsync(conn, tx, "SELECT 1 FROM hosts WHERE id = $id;", j, "jump host", ct).ConfigureAwait(false);
    }

    private static async Task RequireAsync(SqliteConnection conn, SqliteTransaction tx, string sql, Guid? id, string what, CancellationToken ct)
    {
        if (id is { } value && !await Sql.ExistsAsync(conn, tx, sql, ct, ("$id", Sql.Id(value))).ConfigureAwait(false))
            throw new InvalidOperationException($"The referenced {what} {value} does not exist.");
    }

    // ---------------- identities ----------------

    public static async Task<List<Identity>> ReadIdentitiesAsync(SqliteConnection conn, SqliteTransaction? tx, Guid? onlyId, CancellationToken ct)
    {
        const string cols = "id, label, username, kind, passphrase IS NOT NULL, created_at, updated_at";
        await using var cmd = onlyId is { } id
            ? Sql.Command(conn, tx, "SELECT " + cols + " FROM identities WHERE id = $id;", ("$id", Sql.Id(id)))
            : Sql.Command(conn, tx, "SELECT " + cols + " FROM identities ORDER BY label COLLATE NOCASE, id;");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Identity>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new Identity
            {
                Id = Guid.Parse(r.GetString(0)),
                Label = r.GetString(1),
                Username = Sql.StringOrNull(r, 2),
                Kind = (IdentityKind)r.GetInt32(3),
                HasPassphrase = r.GetBoolean(4),
                CreatedAt = Sql.ParseTime(r.GetString(5)),
                UpdatedAt = Sql.ParseTime(r.GetString(6)),
            });
        }
        return list;
    }

    public static async Task<(byte[]? Password, byte[]? PrivateKey, byte[]? Passphrase)?> ReadIdentityBlobsAsync(SqliteConnection conn, Guid id, CancellationToken ct)
    {
        await using var cmd = Sql.Command(conn, null, "SELECT password, private_key, passphrase FROM identities WHERE id = $id;", ("$id", Sql.Id(id)));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return (Sql.BlobOrNull(r, 0), Sql.BlobOrNull(r, 1), Sql.BlobOrNull(r, 2));
    }

    /// <summary><paramref name="secrets"/> null keeps the stored (possibly undecryptable) blobs byte-for-byte.</summary>
    public static async Task UpsertIdentityAsync(SqliteConnection conn, SqliteTransaction tx, Vault vault, Identity identity, IdentitySecrets? secrets, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Label, nameof(identity));
        var idText = Sql.Id(identity.Id);
        await Sql.ExecAsync(conn, tx, """
            INSERT INTO identities (id, label, username, kind, created_at, updated_at)
            VALUES ($id, $label, $username, $kind, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET label = excluded.label, username = excluded.username, kind = excluded.kind,
                created_at = excluded.created_at, updated_at = excluded.updated_at;
            """, ct,
            ("$id", idText), ("$label", identity.Label), ("$username", identity.Username), ("$kind", (int)identity.Kind),
            ("$created", Sql.Time(identity.CreatedAt)), ("$updated", Sql.Time(identity.UpdatedAt))).ConfigureAwait(false);

        if (secrets is null)
            return;
        var pw = EncryptOrNull(vault, IdentitiesTable, identity.Id, FieldPassword, secrets.Password);
        var key = EncryptOrNull(vault, IdentitiesTable, identity.Id, FieldPrivateKey, secrets.PrivateKey);
        var pass = EncryptOrNull(vault, IdentitiesTable, identity.Id, FieldPassphrase, secrets.Passphrase);
        await Sql.ExecAsync(conn, tx, "UPDATE identities SET password = $pw, private_key = $key, passphrase = $pass WHERE id = $id;", ct,
            ("$pw", pw), ("$key", key), ("$pass", pass), ("$id", idText)).ConfigureAwait(false);
        await Undecryptable.ResolveAsync(conn, tx, IdentitiesTable, identity.Id, ct).ConfigureAwait(false);
    }

    // ---------------- proxies ----------------

    public static async Task<List<ProxyProfile>> ReadProxiesAsync(SqliteConnection conn, SqliteTransaction? tx, Guid? onlyId, CancellationToken ct)
    {
        const string cols = "id, label, kind, hostname, port, username, password IS NOT NULL";
        await using var cmd = onlyId is { } id
            ? Sql.Command(conn, tx, "SELECT " + cols + " FROM proxies WHERE id = $id;", ("$id", Sql.Id(id)))
            : Sql.Command(conn, tx, "SELECT " + cols + " FROM proxies ORDER BY label COLLATE NOCASE, id;");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ProxyProfile>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ProxyProfile
            {
                Id = Guid.Parse(r.GetString(0)),
                Label = r.GetString(1),
                Kind = (ProxyKind)r.GetInt32(2),
                Hostname = r.GetString(3),
                Port = r.GetInt32(4),
                Username = Sql.StringOrNull(r, 5),
                HasPassword = r.GetBoolean(6),
            });
        }
        return list;
    }

    public static async Task UpsertProxyAsync(SqliteConnection conn, SqliteTransaction tx, Vault vault, ProxyProfile proxy, string? password, bool replacePassword, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxy.Label, nameof(proxy));
        ArgumentException.ThrowIfNullOrWhiteSpace(proxy.Hostname, nameof(proxy));
        if (proxy.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(proxy), proxy.Port, "Port must be between 1 and 65535.");
        var idText = Sql.Id(proxy.Id);
        await Sql.ExecAsync(conn, tx, """
            INSERT INTO proxies (id, label, kind, hostname, port, username) VALUES ($id, $label, $kind, $host, $port, $user)
            ON CONFLICT(id) DO UPDATE SET label = excluded.label, kind = excluded.kind, hostname = excluded.hostname,
                port = excluded.port, username = excluded.username;
            """, ct,
            ("$id", idText), ("$label", proxy.Label), ("$kind", (int)proxy.Kind), ("$host", proxy.Hostname),
            ("$port", proxy.Port), ("$user", proxy.Username)).ConfigureAwait(false);
        if (!replacePassword)
            return;
        var blob = EncryptOrNull(vault, ProxiesTable, proxy.Id, FieldPassword, password);
        await Sql.ExecAsync(conn, tx, "UPDATE proxies SET password = $pw WHERE id = $id;", ct, ("$pw", blob), ("$id", idText)).ConfigureAwait(false);
        await Undecryptable.ResolveAsync(conn, tx, ProxiesTable, proxy.Id, ct).ConfigureAwait(false);
    }

    // ---------------- groups / known hosts ----------------

    public static async Task<List<HostGroup>> ReadGroupsAsync(SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
    {
        await using var cmd = Sql.Command(conn, tx, "SELECT id, name, parent_id, sort FROM groups ORDER BY sort, name COLLATE NOCASE, id;");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<HostGroup>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new HostGroup { Id = Guid.Parse(r.GetString(0)), Name = r.GetString(1), ParentId = Sql.GuidOrNull(r, 2), Sort = r.GetInt32(3) });
        return list;
    }

    public static Task UpsertGroupRowAsync(SqliteConnection conn, SqliteTransaction tx, HostGroup group, CancellationToken ct) =>
        Sql.ExecAsync(conn, tx, """
            INSERT INTO groups (id, name, parent_id, sort) VALUES ($id, $name, $parent, $sort)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, parent_id = excluded.parent_id, sort = excluded.sort;
            """, ct, ("$id", Sql.Id(group.Id)), ("$name", group.Name), ("$parent", Sql.Id(group.ParentId)), ("$sort", group.Sort));

    public static async Task<List<KnownHostEntry>> ReadKnownHostsAsync(SqliteConnection conn, SqliteTransaction? tx, string? hostPort, CancellationToken ct)
    {
        const string cols = "host_port, key_type, sha256_fingerprint, public_key, first_seen, last_seen";
        await using var cmd = hostPort is null
            ? Sql.Command(conn, tx, "SELECT " + cols + " FROM known_hosts ORDER BY host_port, key_type;")
            : Sql.Command(conn, tx, "SELECT " + cols + " FROM known_hosts WHERE host_port = $hp ORDER BY key_type;", ("$hp", hostPort));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<KnownHostEntry>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new KnownHostEntry
            {
                HostPort = r.GetString(0),
                KeyType = r.GetString(1),
                Sha256Fingerprint = r.GetString(2),
                PublicKeyBase64 = Sql.StringOrNull(r, 3),
                FirstSeen = Sql.ParseTime(r.GetString(4)),
                LastSeen = Sql.ParseTime(r.GetString(5)),
            });
        }
        return list;
    }

    public static Task UpsertKnownHostAsync(SqliteConnection conn, SqliteTransaction? tx, KnownHostEntry e, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(e.HostPort, nameof(e));
        ArgumentException.ThrowIfNullOrWhiteSpace(e.KeyType, nameof(e));
        ArgumentException.ThrowIfNullOrWhiteSpace(e.Sha256Fingerprint, nameof(e));
        // first_seen is kept from the original row; fingerprint/public key/last_seen are replaced.
        return Sql.ExecAsync(conn, tx, """
            INSERT INTO known_hosts (host_port, key_type, sha256_fingerprint, public_key, first_seen, last_seen)
            VALUES ($hp, $kt, $fp, $pk, $first, $last)
            ON CONFLICT(host_port, key_type) DO UPDATE SET sha256_fingerprint = excluded.sha256_fingerprint,
                public_key = excluded.public_key, last_seen = excluded.last_seen;
            """, ct, ("$hp", e.HostPort), ("$kt", e.KeyType), ("$fp", e.Sha256Fingerprint), ("$pk", e.PublicKeyBase64),
            ("$first", Sql.Time(e.FirstSeen)), ("$last", Sql.Time(e.LastSeen)));
    }

    private static byte[]? EncryptOrNull(Vault vault, string table, Guid id, string field, string? value) =>
        value is null ? null : vault.Encrypt(table, id, field, value);

    /// <summary>Decrypt one field; on failure flag it (original row untouched) and throw <see cref="SecretUnavailableException"/>.</summary>
    public static async Task<string?> DecryptOrFlagAsync(StorageDatabase db, Vault vault, string table, Guid id, string field, byte[]? blob, CancellationToken ct)
    {
        if (blob is null)
            return null;
        try
        {
            return vault.Decrypt(table, id, field, blob);
        }
        catch (CryptographicException e)
        {
            await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
            await Undecryptable.FlagAsync(conn, null, table, id, field, e.Message, blob, ct).ConfigureAwait(false);
            throw new SecretUnavailableException(table, id, field, e);
        }
    }
}
