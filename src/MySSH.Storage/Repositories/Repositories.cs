using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MySSH.Core;

namespace MySSH.Storage;

internal sealed class HostRepository(StorageDatabase db) : IHostRepository
{
    public async Task<IReadOnlyList<Host>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        return await Rows.ReadHostsAsync(conn, null, null, ct).ConfigureAwait(false);
    }

    public async Task<Host?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        return (await Rows.ReadHostsAsync(conn, null, id, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task SaveAsync(Host host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        await Rows.RequireJumpHostsAsync(conn, tx, host, ct).ConfigureAwait(false);
        await Rows.UpsertHostAsync(conn, tx, host, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        var users = await Sql.GuidsAsync(conn, tx,
            "SELECT DISTINCT host_id FROM host_jumps WHERE jump_host_id = $id AND host_id <> $id ORDER BY host_id;", ct,
            ("$id", Sql.Id(id))).ConfigureAwait(false);
        if (users.Count > 0)
            throw new EntityInUseException($"Host {id} is used as a jump host by {users.Count} other host(s).", users);
        await Sql.ExecAsync(conn, tx, "DELETE FROM hosts WHERE id = $id;", ct, ("$id", Sql.Id(id))).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}

internal sealed class IdentityRepository(StorageDatabase db, Vault vault) : IIdentityRepository
{
    public async Task<IReadOnlyList<Identity>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        return await Rows.ReadIdentitiesAsync(conn, null, null, ct).ConfigureAwait(false);
    }

    public async Task<Identity?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        return (await Rows.ReadIdentitiesAsync(conn, null, id, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    /// <exception cref="VaultLockedException"/>
    /// <exception cref="SecretUnavailableException">A stored secret failed to decrypt; it has been flagged and left untouched.</exception>
    public async Task<IdentitySecrets?> GetSecretsAsync(Guid id, CancellationToken ct = default)
    {
        vault.EnsureUnlocked();
        (byte[]? Password, byte[]? PrivateKey, byte[]? Passphrase)? blobs;
        await using (var conn = await db.OpenAsync(ct).ConfigureAwait(false))
            blobs = await Rows.ReadIdentityBlobsAsync(conn, id, ct).ConfigureAwait(false);
        if (blobs is not { } b)
            return null;
        var pw = await Rows.DecryptOrFlagAsync(db, vault, Rows.IdentitiesTable, id, Rows.FieldPassword, b.Password, ct).ConfigureAwait(false);
        var key = await Rows.DecryptOrFlagAsync(db, vault, Rows.IdentitiesTable, id, Rows.FieldPrivateKey, b.PrivateKey, ct).ConfigureAwait(false);
        var pass = await Rows.DecryptOrFlagAsync(db, vault, Rows.IdentitiesTable, id, Rows.FieldPassphrase, b.Passphrase, ct).ConfigureAwait(false);
        return new IdentitySecrets(pw, key, pass);
    }

    public async Task SaveAsync(Identity identity, IdentitySecrets? secrets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (secrets is not null)
            vault.EnsureUnlocked();
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        await Rows.UpsertIdentityAsync(conn, tx, vault, identity, secrets, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        var users = await Sql.GuidsAsync(conn, tx,
            "SELECT id FROM hosts WHERE identity_id = $id OR ai_identity_id = $id ORDER BY id;", ct, ("$id", Sql.Id(id))).ConfigureAwait(false);
        if (users.Count > 0)
            throw new EntityInUseException($"Identity {id} is used by {users.Count} host(s).", users);
        await Sql.ExecAsync(conn, tx, "DELETE FROM identities WHERE id = $id;", ct, ("$id", Sql.Id(id))).ConfigureAwait(false);
        await Undecryptable.ForgetAsync(conn, tx, Rows.IdentitiesTable, id, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}

internal sealed class ProxyRepository(StorageDatabase db, Vault vault) : IProxyRepository
{
    public async Task<IReadOnlyList<ProxyProfile>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        return await Rows.ReadProxiesAsync(conn, null, null, ct).ConfigureAwait(false);
    }

    public async Task<ProxyProfile?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        return (await Rows.ReadProxiesAsync(conn, null, id, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<string?> GetPasswordAsync(Guid id, CancellationToken ct = default)
    {
        vault.EnsureUnlocked();
        byte[]? blob;
        await using (var conn = await db.OpenAsync(ct).ConfigureAwait(false))
            blob = (byte[]?)await Sql.ScalarAsync(conn, null, "SELECT password FROM proxies WHERE id = $id;", ct, ("$id", Sql.Id(id))).ConfigureAwait(false);
        return await Rows.DecryptOrFlagAsync(db, vault, Rows.ProxiesTable, id, Rows.FieldPassword, blob, ct).ConfigureAwait(false);
    }

    /// <summary><paramref name="password"/> null keeps the stored password; an empty string removes it.</summary>
    public async Task SaveAsync(ProxyProfile proxy, string? password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        if (!string.IsNullOrEmpty(password))
            vault.EnsureUnlocked();
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        await Rows.UpsertProxyAsync(conn, tx, vault, proxy, string.IsNullOrEmpty(password) ? null : password, replacePassword: password is not null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        var users = await Sql.GuidsAsync(conn, tx, "SELECT id FROM hosts WHERE proxy_id = $id ORDER BY id;", ct, ("$id", Sql.Id(id))).ConfigureAwait(false);
        if (users.Count > 0)
            throw new EntityInUseException($"Proxy {id} is used by {users.Count} host(s).", users);
        await Sql.ExecAsync(conn, tx, "DELETE FROM proxies WHERE id = $id;", ct, ("$id", Sql.Id(id))).ConfigureAwait(false);
        await Undecryptable.ForgetAsync(conn, tx, Rows.ProxiesTable, id, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}

internal sealed class GroupRepository(StorageDatabase db) : IGroupRepository
{
    public async Task<IReadOnlyList<HostGroup>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        return await Rows.ReadGroupsAsync(conn, null, ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(HostGroup group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(group.Name, nameof(group));
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        // Parent must exist and must not be the group itself or one of its descendants.
        var parent = group.ParentId;
        var guard = 0;
        while (parent is { } p)
        {
            if (p == group.Id)
                throw new ArgumentException("A group cannot be its own ancestor.", nameof(group));
            if (++guard > 10_000)
                throw new InvalidOperationException("Group hierarchy is too deep or cyclic.");
            var next = await Sql.ScalarAsync(conn, tx, "SELECT COALESCE(parent_id, '') FROM groups WHERE id = $id;", ct, ("$id", Sql.Id(p))).ConfigureAwait(false);
            if (next is null)
                throw new InvalidOperationException($"The parent group {p} does not exist.");
            parent = next is string s && s.Length > 0 ? Guid.Parse(s) : null;
        }
        await Rows.UpsertGroupRowAsync(conn, tx, group, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Child groups and hosts move up to the deleted group's parent.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        var idText = Sql.Id(id);
        await Sql.ExecAsync(conn, tx, "UPDATE groups SET parent_id = (SELECT parent_id FROM groups WHERE id = $id) WHERE parent_id = $id;", ct, ("$id", idText)).ConfigureAwait(false);
        await Sql.ExecAsync(conn, tx, "UPDATE hosts SET group_id = (SELECT parent_id FROM groups WHERE id = $id) WHERE group_id = $id;", ct, ("$id", idText)).ConfigureAwait(false);
        await Sql.ExecAsync(conn, tx, "DELETE FROM groups WHERE id = $id;", ct, ("$id", idText)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}

internal sealed class KnownHostStore(StorageDatabase db) : IKnownHostStore
{
    public async Task<IReadOnlyList<KnownHostEntry>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        return await Rows.ReadKnownHostsAsync(conn, null, null, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnownHostEntry>> FindAsync(string hostPort, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostPort);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        return await Rows.ReadKnownHostsAsync(conn, null, hostPort, ct).ConfigureAwait(false);
    }

    public async Task UpsertAsync(KnownHostEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await Rows.UpsertKnownHostAsync(conn, null, entry, ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string hostPort, string keyType, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await Sql.ExecAsync(conn, null, "DELETE FROM known_hosts WHERE host_port = $hp AND key_type = $kt;", ct, ("$hp", hostPort), ("$kt", keyType)).ConfigureAwait(false);
    }
}

internal sealed class SettingsStore(StorageDatabase db) : ISettingsStore
{
    public async Task<string?> GetJsonAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        return (string?)await Sql.ScalarAsync(conn, null, "SELECT value FROM settings WHERE key = $k;", ct, ("$k", key)).ConfigureAwait(false);
    }

    public async Task SetJsonAsync(string key, string json, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new ArgumentException("Setting value must be valid JSON.", nameof(json), e);
        }
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await Sql.ExecAsync(conn, null, """
            INSERT INTO settings (key, value, updated_at) VALUES ($k, $v, $t)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """, ct, ("$k", key), ("$v", json), ("$t", Sql.Time(DateTimeOffset.UtcNow))).ConfigureAwait(false);
    }

    public async Task<T?> GetAsync<T>(string key, JsonTypeInfo<T> typeInfo, CancellationToken ct = default)
    {
        var json = await GetJsonAsync(key, ct).ConfigureAwait(false);
        return json is null ? default : JsonSerializer.Deserialize(json, typeInfo);
    }

    public Task SetAsync<T>(string key, T value, JsonTypeInfo<T> typeInfo, CancellationToken ct = default) =>
        SetJsonAsync(key, JsonSerializer.Serialize(value, typeInfo), ct);

    public async Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        return await Sql.ExecAsync(conn, null, "DELETE FROM settings WHERE key = $k;", ct, ("$k", key)).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Sql.Command(conn, null, "SELECT key FROM settings ORDER BY key;");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<string>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(r.GetString(0));
        return list;
    }
}
