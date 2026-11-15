using Microsoft.Data.Sqlite;
using MySSH.Core;

namespace MySSH.Storage;

/// <summary>Writes an <see cref="ImportBatch"/> in ONE transaction (all or nothing), fixing dangling references with warnings.</summary>
internal static class ImportWriter
{
    public static async Task<ImportResult> WriteAsync(StorageDatabase db, Vault vault, ImportBatch batch, ImportMode mode, ImportSource source, CancellationToken ct)
    {
        vault.EnsureUnlocked();
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);

        var groupIds = await IdsAsync(conn, tx, "SELECT id FROM groups;", ct).ConfigureAwait(false);
        var identityIds = await IdsAsync(conn, tx, "SELECT id FROM identities;", ct).ConfigureAwait(false);
        var proxyIds = await IdsAsync(conn, tx, "SELECT id FROM proxies;", ct).ConfigureAwait(false);
        var hostIds = await IdsAsync(conn, tx, "SELECT id FROM hosts;", ct).ConfigureAwait(false);
        var existingGroups = new HashSet<Guid>(groupIds);
        var existingIdentities = new HashSet<Guid>(identityIds);
        var existingProxies = new HashSet<Guid>(proxyIds);
        var existingHosts = new HashSet<Guid>(hostIds);
        groupIds.UnionWith(batch.Groups.Select(g => g.Id));
        identityIds.UnionWith(batch.Identities.Select(i => i.Identity.Id));
        proxyIds.UnionWith(batch.Proxies.Select(p => p.Proxy.Id));
        hostIds.UnionWith(batch.Hosts.Select(h => h.Id));

        var groups = new Counter();
        foreach (var g0 in batch.Groups)
        {
            var g = g0;
            if (g.ParentId is { } p && (!groupIds.Contains(p) || p == g.Id))
            {
                batch.Warn(Sql.Id(g.Id), g.Name, $"Parent group {p} not found; the group was placed at the top level.");
                g = g with { ParentId = null };
            }
            if (Decide(mode, existingGroups.Contains(g.Id), groups))
                await Rows.UpsertGroupRowAsync(conn, tx, g, ct).ConfigureAwait(false);
        }

        var identities = new Counter();
        foreach (var (identity, secrets) in batch.Identities)
        {
            if (Decide(mode, existingIdentities.Contains(identity.Id), identities))
                await Rows.UpsertIdentityAsync(conn, tx, vault, identity, secrets, ct).ConfigureAwait(false);
        }

        var proxies = new Counter();
        foreach (var (proxy, password) in batch.Proxies)
        {
            if (Decide(mode, existingProxies.Contains(proxy.Id), proxies))
                await Rows.UpsertProxyAsync(conn, tx, vault, proxy, password, replacePassword: password is not null, ct).ConfigureAwait(false);
        }

        var hosts = new Counter();
        foreach (var h0 in batch.Hosts)
        {
            var h = FixReferences(h0, batch, identityIds, proxyIds, groupIds, hostIds);
            if (Decide(mode, existingHosts.Contains(h.Id), hosts))
                await Rows.UpsertHostAsync(conn, tx, h, ct).ConfigureAwait(false);
        }

        var known = new Counter();
        foreach (var k in batch.KnownHosts)
        {
            var exists = await Sql.ExistsAsync(conn, tx, "SELECT 1 FROM known_hosts WHERE host_port = $hp AND key_type = $kt;", ct,
                ("$hp", k.HostPort), ("$kt", k.KeyType)).ConfigureAwait(false);
            if (Decide(mode, exists, known))
                await Rows.UpsertKnownHostAsync(conn, tx, k, ct).ConfigureAwait(false);
        }

        var settings = new Counter();
        foreach (var (key, json) in batch.Settings)
        {
            var exists = await Sql.ExistsAsync(conn, tx, "SELECT 1 FROM settings WHERE key = $k;", ct, ("$k", key)).ConfigureAwait(false);
            if (Decide(mode, exists, settings))
            {
                await Sql.ExecAsync(conn, tx, """
                    INSERT INTO settings (key, value, updated_at) VALUES ($k, $v, $t)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                    """, ct, ("$k", key), ("$v", json), ("$t", Sql.Time(DateTimeOffset.UtcNow))).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new ImportResult
        {
            Source = source,
            Groups = groups.ToCounts(),
            Identities = identities.ToCounts(),
            Proxies = proxies.ToCounts(),
            Hosts = hosts.ToCounts(),
            KnownHosts = known.ToCounts(),
            Settings = settings.ToCounts(),
            FailedRecords = batch.FailedRecords,
            Issues = [.. batch.Issues],
            IsWeakLegacyFormat = source == ImportSource.LegacyBackupV1,
        };
    }

    private static Host FixReferences(Host h, ImportBatch batch, HashSet<Guid> identities, HashSet<Guid> proxies, HashSet<Guid> groups, HashSet<Guid> hosts)
    {
        var id = Sql.Id(h.Id);
        if (h.IdentityId is { } i && !identities.Contains(i))
        {
            batch.Warn(id, h.Label, $"Identity {i} not found; the host was imported without credentials.");
            h = h with { IdentityId = null };
        }
        if (h.AiIdentityId is { } ai && !identities.Contains(ai))
        {
            batch.Warn(id, h.Label, $"AI identity {ai} not found and was cleared.");
            h = h with { AiIdentityId = null };
        }
        if (h.ProxyId is { } p && !proxies.Contains(p))
        {
            batch.Warn(id, h.Label, $"Proxy {p} not found and was cleared.");
            h = h with { ProxyId = null };
        }
        if (h.GroupId is { } g && !groups.Contains(g))
        {
            batch.Warn(id, h.Label, $"Group {g} not found; the host was placed at the top level.");
            h = h with { GroupId = null };
        }
        if (h.JumpChain.Any(j => j == h.Id || !hosts.Contains(j)))
        {
            foreach (var j in h.JumpChain.Where(j => j == h.Id || !hosts.Contains(j)))
                batch.Warn(id, h.Label, j == h.Id ? "The host referenced itself as jump host; the reference was removed." : $"Jump host {j} not found; it was removed from the jump chain.");
            h = h with { JumpChain = [.. h.JumpChain.Where(j => j != h.Id && hosts.Contains(j))] };
        }
        return h;
    }

    private static bool Decide(ImportMode mode, bool exists, Counter c)
    {
        if (!exists) { c.Added++; return true; }
        if (mode == ImportMode.Overwrite) { c.Updated++; return true; }
        c.Skipped++;
        return false;
    }

    private static async Task<HashSet<Guid>> IdsAsync(SqliteConnection conn, SqliteTransaction tx, string sql, CancellationToken ct) =>
        [.. await Sql.GuidsAsync(conn, tx, sql, ct).ConfigureAwait(false)];

    private sealed class Counter
    {
        public int Added;
        public int Updated;
        public int Skipped;
        public EntityImportCounts ToCounts() => new(Added, Updated, Skipped);
    }
}
