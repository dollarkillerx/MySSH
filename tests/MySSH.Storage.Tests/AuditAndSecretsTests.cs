using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MySSH.Core;

namespace MySSH.Storage.Tests;

public class SchemaV2MigrationTests
{
    /// <summary>Builds a database exactly as a v1 (P1/P2) MySSH left it: migration 1 only, plus some data.</summary>
    private static async Task<(Guid HostId, Guid JumpId)> CreateV1DatabaseAsync(TempDir dir)
    {
        Directory.CreateDirectory(dir.AppPaths.DataDirectory);
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dir.AppPaths.DatabasePath, Pooling = false }.ToString());
        await conn.OpenAsync(T.Ct);
        async Task Exec(string sql, params (string, object?)[] ps)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in ps)
                cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(T.Ct);
        }
        await Exec("CREATE TABLE schema_version (version INTEGER NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL);");
        await Exec(Migrations.All[0].Sql);
        await Exec("INSERT INTO schema_version (version, applied_at) VALUES (1, '2026-01-01T00:00:00.0000000+00:00');");
        var host = Guid.NewGuid();
        var jump = Guid.NewGuid();
        const string now = "2026-01-01T00:00:00.0000000+00:00";
        foreach (var (id, label) in new[] { (jump, "jump"), (host, "web") })
        {
            await Exec("""
                INSERT INTO hosts (id, label, hostname, port, username, tags, ai_policy, notes, created_at, updated_at)
                VALUES ($id, $l, 'h.example', 2222, 'deploy', '["prod"]', 2, 'n', $t, $t);
                """, ("$id", id.ToString()), ("$l", label), ("$t", now));
        }
        await Exec("INSERT INTO host_jumps (host_id, position, jump_host_id) VALUES ($h, 0, $j);", ("$h", host.ToString()), ("$j", jump.ToString()));
        await Exec("INSERT INTO settings (key, value, updated_at) VALUES ('ui.theme', '\"g10\"', $t);", ("$t", now));
        return (host, jump);
    }

    [Fact]
    public async Task V1_database_is_migrated_to_the_latest_schema_keeping_existing_data()
    {
        using var dir = new TempDir();
        var (hostId, jumpId) = await CreateV1DatabaseAsync(dir);

        await using var s = await T.OpenUnlockedAsync(dir);
        Assert.Equal(3, s.SchemaVersion);
        Assert.Equal(3L, await T.ScalarAsync(s, "SELECT COUNT(*) FROM schema_version;"));
        var h = await s.Hosts.GetAsync(hostId, T.Ct);
        Assert.NotNull(h);
        Assert.Equal(("web", "h.example", 2222, "deploy", AiPolicy.Ask, "n"), (h.Label, h.Hostname, h.Port, h.Username, h.AiPolicy, h.Notes));
        Assert.Equal(["prod"], h.Tags);
        Assert.Equal([jumpId], h.JumpChain);
        Assert.Empty(h.AiCommandAllowlist);
        Assert.Equal(HostProtocol.Ssh, h.Protocol); // v3: existing hosts are SSH
        Assert.Equal("[]", await T.ScalarAsync(s, "SELECT ai_command_allowlist FROM hosts WHERE id = $id;", ("$id", hostId.ToString())));
        Assert.Equal("\"g10\"", await s.Settings.GetJsonAsync("ui.theme", T.Ct));

        // New column and tables are usable right away.
        await s.Hosts.SaveAsync(h with { AiCommandAllowlist = ["uptime"] }, T.Ct);
        Assert.Equal(["uptime"], (await s.Hosts.GetAsync(hostId, T.Ct))!.AiCommandAllowlist);
        await s.AuditLog.AppendAsync(AuditTests.Entry(DateTimeOffset.UtcNow), T.Ct);
        await s.Secrets.SetAsync("k", "v", T.Ct);
        Assert.Equal("v", await s.Secrets.GetAsync("k", T.Ct));
    }

    [Fact]
    public async Task Allowlist_column_rejects_invalid_json()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var h = T.NewHost("a");
        await s.Hosts.SaveAsync(h, T.Ct);
        await Assert.ThrowsAsync<SqliteException>(() =>
            T.ExecAsync(s, "UPDATE hosts SET ai_command_allowlist = 'not json' WHERE id = $id;", ("$id", h.Id.ToString())));
    }

    [Fact]
    public async Task Allowlist_is_trimmed_and_deduplicated_on_save()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var h = T.NewHost("a") with { AiCommandAllowlist = ["  df -h ", "", "uptime", "df -h", "   "] };
        await s.Hosts.SaveAsync(h, T.Ct);
        Assert.Equal(["df -h", "uptime"], (await s.Hosts.GetAsync(h.Id, T.Ct))!.AiCommandAllowlist);
    }

    [Fact]
    public async Task Older_v2_backup_without_allowlist_imports_with_empty_allowlist()
    {
        using var dir = new TempDir();
        var host = new BackupHost
        {
            Id = Guid.NewGuid(), Label = "old", Hostname = "old.example", Port = 22, AiPolicy = AiPolicy.ReadOnly,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var payload = new BackupPayload { CreatedAt = DateTimeOffset.UtcNow, Hosts = [host] };
        var json = JsonSerializer.Serialize(payload, StorageJsonContext.Default.BackupPayload);
        Assert.Contains("\"aiCommandAllowlist\":null", json, StringComparison.Ordinal);
        json = json.Replace(",\"aiCommandAllowlist\":null", "", StringComparison.Ordinal);
        Assert.DoesNotContain("aiCommandAllowlist", json, StringComparison.Ordinal);
        var bytes = await BackupService.EncryptV2Async(Encoding.UTF8.GetBytes(json), "pw", T.FastArgon2, T.Ct);

        await using var s = await T.OpenUnlockedAsync(dir);
        var r = await s.Backup.ImportAsync(new MemoryStream(bytes), "pw", T.Ct);
        Assert.Equal(new EntityImportCounts(1, 0, 0), r.Hosts);
        var loaded = await s.Hosts.GetAsync(host.Id, T.Ct);
        Assert.NotNull(loaded);
        Assert.Equal(AiPolicy.ReadOnly, loaded.AiPolicy);
        Assert.Empty(loaded.AiCommandAllowlist);
    }

    [Fact]
    public async Task Backup_round_trip_keeps_allowlist_and_never_contains_secret_settings()
    {
        using var dirA = new TempDir();
        using var dirB = new TempDir();
        var h = T.NewHost("ro") with { AiPolicy = AiPolicy.ReadOnly, AiCommandAllowlist = ["df -h", "journalctl -u nginx"] };
        var output = new MemoryStream();
        await using (var a = await T.OpenUnlockedAsync(dirA))
        {
            await a.Hosts.SaveAsync(h, T.Ct);
            await a.Secrets.SetAsync("mcp.token", "token-value-never-exported", T.Ct);
            await a.Backup.ExportAsync(output, "pw", ct: T.Ct);
        }
        var plain = await BackupService.DecryptV2Async(output.ToArray(), "pw", T.Ct);
        Assert.True(plain.AsSpan().IndexOf("token-value-never-exported"u8) < 0);
        Assert.True(plain.AsSpan().IndexOf("mcp.token"u8) < 0);

        await using var b = await T.OpenUnlockedAsync(dirB);
        await b.Backup.ImportAsync(new MemoryStream(output.ToArray()), "pw", T.Ct);
        Assert.Equal(h.AiCommandAllowlist, (await b.Hosts.GetAsync(h.Id, T.Ct))!.AiCommandAllowlist);
        Assert.Null(await b.Secrets.GetAsync("mcp.token", T.Ct));
    }
}

public class AuditTests
{
    internal static AuditEntry Entry(DateTimeOffset ts, string actor = "mcp:claude-code", string tool = "run_command", Guid? host = null,
        AuditDecision decision = AuditDecision.Allowed, int? exit = 0, string? error = null) =>
        new(Guid.NewGuid(), ts, actor, tool, host, """{"command":"uptime"}""", decision, exit, 42, error);

    [Fact]
    public async Task Append_and_query_round_trip_all_fields_newest_first()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var t0 = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.FromHours(9)).AddTicks(7);
        var host = Guid.NewGuid();
        var a = Entry(t0, host: host, decision: AuditDecision.Approved, exit: 3, error: "boom");
        var b = Entry(t0.AddSeconds(1), actor: "ai:claude/opus", tool: "sftp_read", exit: null);
        await s.AuditLog.AppendAsync(a, T.Ct);
        await s.AuditLog.AppendAsync(b, T.Ct);

        var all = await s.AuditLog.QueryAsync(new AuditQuery(), T.Ct);
        Assert.Equal([b.Id, a.Id], all.Select(e => e.Id));
        var ra = all[1];
        Assert.Equal(a.Timestamp, ra.Timestamp); // same instant (stored as UTC ticks)
        Assert.Equal(TimeSpan.Zero, ra.Timestamp.Offset);
        Assert.Equal(a with { Timestamp = ra.Timestamp }, ra);
        Assert.Null(all[0].HostId);
        Assert.Null(all[0].ExitCode);
    }

    [Fact]
    public async Task Query_pages_with_before_and_filters_by_host_and_actor_prefix()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        var h1 = Guid.NewGuid();
        var h2 = Guid.NewGuid();
        var entries = new List<AuditEntry>();
        for (var i = 0; i < 25; i++)
        {
            var e = Entry(t0.AddSeconds(i), actor: i % 2 == 0 ? "mcp:client" : "ai:openai/gpt", host: i % 3 == 0 ? h1 : h2);
            entries.Add(e);
            await s.AuditLog.AppendAsync(e, T.Ct);
        }

        // Paging: 10 + 10 + 5, newest first, no overlap.
        var seen = new List<Guid>();
        DateTimeOffset? before = null;
        for (var page = 0; page < 4; page++)
        {
            var p = await s.AuditLog.QueryAsync(new AuditQuery { Limit = 10, Before = before }, T.Ct);
            if (p.Count == 0) break;
            seen.AddRange(p.Select(e => e.Id));
            before = p[^1].Timestamp;
        }
        Assert.Equal(entries.AsEnumerable().Reverse().Select(e => e.Id), seen);

        var byHost = await s.AuditLog.QueryAsync(new AuditQuery { HostId = h1 }, T.Ct);
        Assert.Equal(entries.Count(e => e.HostId == h1), byHost.Count);
        Assert.All(byHost, e => Assert.Equal(h1, e.HostId));

        var mcp = await s.AuditLog.QueryAsync(new AuditQuery { ActorPrefix = "mcp:" }, T.Ct);
        Assert.Equal(13, mcp.Count);
        Assert.All(mcp, e => Assert.StartsWith("mcp:", e.Actor, StringComparison.Ordinal));

        var both = await s.AuditLog.QueryAsync(new AuditQuery { ActorPrefix = "ai:", HostId = h1 }, T.Ct);
        Assert.Equal(entries.Count(e => e.HostId == h1 && e.Actor.StartsWith("ai:", StringComparison.Ordinal)), both.Count);

        // LIKE wildcards in the prefix are literal.
        Assert.Empty(await s.AuditLog.QueryAsync(new AuditQuery { ActorPrefix = "%" }, T.Ct));
        Assert.Empty(await s.AuditLog.QueryAsync(new AuditQuery { Limit = 0 }, T.Ct));
    }

    [Fact]
    public async Task Prune_removes_only_older_entries_and_entries_survive_host_deletion()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var host = T.NewHost("gone");
        await s.Hosts.SaveAsync(host, T.Ct);
        var now = DateTimeOffset.UtcNow;
        await s.AuditLog.AppendAsync(Entry(now.AddDays(-40), host: host.Id), T.Ct);
        await s.AuditLog.AppendAsync(Entry(now.AddDays(-31), host: host.Id), T.Ct);
        await s.AuditLog.AppendAsync(Entry(now.AddDays(-1), host: host.Id), T.Ct);
        await s.Hosts.DeleteAsync(host.Id, T.Ct);

        Assert.Equal(3, (await s.AuditLog.QueryAsync(new AuditQuery { HostId = host.Id }, T.Ct)).Count);
        Assert.Equal(2, await s.AuditLog.PruneAsync(now.AddDays(-30), T.Ct));
        Assert.Single(await s.AuditLog.QueryAsync(new AuditQuery(), T.Ct));
        Assert.Equal(0, await s.AuditLog.PruneAsync(now.AddDays(-30), T.Ct));
    }

    [Fact]
    public async Task Audit_log_works_while_the_vault_is_locked()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        s.Vault.Lock();
        await s.AuditLog.AppendAsync(Entry(DateTimeOffset.UtcNow), T.Ct);
        Assert.Single(await s.AuditLog.QueryAsync(new AuditQuery(), T.Ct));
    }
}

public class SecretSettingsTests
{
    private static async Task<byte[]> RawDatabaseBytesAsync(MySshStorage s)
    {
        var bytes = new List<byte>();
        foreach (var path in new[] { s.Paths.DatabasePath, s.Paths.DatabasePath + "-wal" })
        {
            if (!File.Exists(path)) continue;
            await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[f.Length];
            await f.ReadExactlyAsync(buf, T.Ct);
            bytes.AddRange(buf);
        }
        return [.. bytes];
    }

    [Fact]
    public async Task Set_get_overwrite_remove()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        Assert.Null(await s.Secrets.GetAsync("mcp.token", T.Ct));
        await s.Secrets.SetAsync("mcp.token", "first ✓", T.Ct);
        Assert.Equal("first ✓", await s.Secrets.GetAsync("mcp.token", T.Ct));
        await s.Secrets.SetAsync("mcp.token", "second", T.Ct);
        Assert.Equal("second", await s.Secrets.GetAsync("mcp.token", T.Ct));
        await s.Secrets.SetAsync("empty", "", T.Ct);
        Assert.Equal("", await s.Secrets.GetAsync("empty", T.Ct));
        Assert.True(await s.Secrets.RemoveAsync("mcp.token", T.Ct));
        Assert.False(await s.Secrets.RemoveAsync("mcp.token", T.Ct));
        Assert.Null(await s.Secrets.GetAsync("mcp.token", T.Ct));
        // Not visible through the plain settings store.
        Assert.Empty(await s.Settings.ListKeysAsync(T.Ct));
    }

    [Fact]
    public async Task Values_are_encrypted_at_rest_and_survive_reopen()
    {
        using var dir = new TempDir();
        var ks = new InMemoryKeyStore();
        const string secret = "Bearer-PLAINTEXT-7f3c9a2e51";
        await using (var s = await T.OpenUnlockedAsync(dir, ks))
        {
            await s.Secrets.SetAsync("mcp.token", secret, T.Ct);
            var blob = (byte[])(await T.ScalarAsync(s, "SELECT value FROM secret_settings WHERE key = 'mcp.token';"))!;
            Assert.True(blob.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0);
            Assert.Equal(FieldCipher.Overhead + secret.Length, blob.Length);
            Assert.True((await RawDatabaseBytesAsync(s)).AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0);
        }
        await using (var s = await T.OpenUnlockedAsync(dir, ks))
            Assert.Equal(secret, await s.Secrets.GetAsync("mcp.token", T.Ct));
    }

    [Fact]
    public async Task Locked_vault_fails_cleanly()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        await s.Secrets.SetAsync("k", "v", T.Ct);
        s.Vault.Lock();
        await Assert.ThrowsAsync<VaultLockedException>(() => s.Secrets.GetAsync("k", T.Ct));
        await Assert.ThrowsAsync<VaultLockedException>(() => s.Secrets.SetAsync("k", "w", T.Ct));
        Assert.Equal(1L, await T.ScalarAsync(s, "SELECT COUNT(*) FROM secret_settings;")); // nothing written or lost
        Assert.Equal(VaultUnlockResult.Unlocked, await s.Vault.UnlockAsync(T.Ct));
        Assert.Equal("v", await s.Secrets.GetAsync("k", T.Ct));
    }

    [Fact]
    public async Task Blob_moved_to_another_key_fails_to_decrypt_and_is_flagged_not_dropped()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        await s.Secrets.SetAsync("a", "alpha", T.Ct);
        await s.Secrets.SetAsync("b", "beta", T.Ct);
        await T.ExecAsync(s, "UPDATE secret_settings SET value = (SELECT value FROM secret_settings WHERE key = 'a') WHERE key = 'b';");
        var e = await Assert.ThrowsAsync<SecretUnavailableException>(() => s.Secrets.GetAsync("b", T.Ct));
        Assert.Equal("secret_settings", e.Table);
        var flagged = Assert.Single(await s.UndecryptableRecords.ListAsync(ct: T.Ct));
        Assert.Equal(("secret_settings", SecretSettingsStore.RecordId("b")), (flagged.Table, flagged.RecordId));
        Assert.Equal(2L, await T.ScalarAsync(s, "SELECT COUNT(*) FROM secret_settings;"));
        Assert.Equal("alpha", await s.Secrets.GetAsync("a", T.Ct));

        // Scan finds it too; replacing the value resolves the flag.
        Assert.Single(await s.UndecryptableRecords.ScanAsync(T.Ct));
        await s.Secrets.SetAsync("b", "beta2", T.Ct);
        Assert.Empty(await s.UndecryptableRecords.ListAsync(ct: T.Ct));
        Assert.Equal("beta2", await s.Secrets.GetAsync("b", T.Ct));
    }
}
