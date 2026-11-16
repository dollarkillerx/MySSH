using MySSH.Core;

namespace MySSH.Storage.Tests;

public class MigrationTests
{
    [Fact]
    public async Task Fresh_database_is_migrated_with_wal_and_foreign_keys()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        Assert.Equal(3, s.SchemaVersion);
        Assert.Equal(3L, await T.ScalarAsync(s, "SELECT MAX(version) FROM schema_version;"));
        Assert.Equal("wal", await T.ScalarAsync(s, "PRAGMA journal_mode;"));
        foreach (var table in new[] { "hosts", "host_jumps", "identities", "proxies", "groups", "known_hosts", "settings", "undecryptable_fields", "audit_log", "secret_settings" })
            Assert.Equal(1L, await T.ScalarAsync(s, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $n;", ("$n", table)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(s.Paths.DatabasePath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(s.Paths.DataDirectory));
        }
    }

    [Fact]
    public async Task Foreign_keys_are_enforced_by_storage_connections()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var a = T.NewHost("a");
        var b = T.NewHost("b") with { JumpChain = [a.Id] };
        await s.Hosts.SaveAsync(a, T.Ct);
        await s.Hosts.SaveAsync(b, T.Ct);
        // Bypass the repository check: the FK itself must refuse to orphan host_jumps (via a storage connection).
        var db = new StorageDatabase(s.Paths.DatabasePath);
        await using var conn = await db.OpenAsync(T.Ct);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            Sql.ExecAsync(conn, null, "DELETE FROM hosts WHERE id = $id;", T.Ct, ("$id", a.Id.ToString())));
    }

    [Fact]
    public async Task Reopening_does_not_reapply_migrations_and_keeps_data()
    {
        using var dir = new TempDir();
        var ks = new InMemoryKeyStore();
        var h = T.NewHost("h");
        await using (var s = await T.OpenUnlockedAsync(dir, ks))
            await s.Hosts.SaveAsync(h, T.Ct);
        await using (var s = await T.OpenUnlockedAsync(dir, ks))
        {
            Assert.Equal(3L, await T.ScalarAsync(s, "SELECT COUNT(*) FROM schema_version;"));
            Assert.NotNull(await s.Hosts.GetAsync(h.Id, T.Ct));
        }
    }

    [Fact]
    public async Task Database_from_a_newer_version_is_refused()
    {
        using var dir = new TempDir();
        await using (var s = await T.OpenUnlockedAsync(dir))
            await T.ExecAsync(s, "INSERT INTO schema_version (version, applied_at) VALUES (999, 'x');");
        await Assert.ThrowsAsync<StorageSchemaTooNewException>(() => MySshStorage.OpenAsync(T.Options(dir, new InMemoryKeyStore()), T.Ct));
    }
}

public class AppPathsTests
{
    private static Func<string, string?> Env(params (string, string)[] vars) =>
        name => vars.FirstOrDefault(v => v.Item1 == name).Item2;

    [Fact]
    public void MacOS_uses_application_support_and_does_not_collide_with_legacy()
    {
        var p = AppPaths.Resolve(AppPlatform.MacOS, Env(), "/Users/me");
        Assert.Equal("/Users/me/Library/Application Support/MySSH.Desktop", p.DataDirectory);
        Assert.Equal("/Users/me/Library/Application Support/myssh", p.LegacyDataDirectory);
        Assert.False(string.Equals(p.DataDirectory, p.LegacyDataDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(p.DataDirectory, "myssh.db"), p.DatabasePath);
        Assert.Equal(Path.Combine(p.DataDirectory, "vault.json"), p.VaultFilePath);
    }

    [Fact]
    public void Windows_uses_roaming_appdata()
    {
        var p = AppPaths.Resolve(AppPlatform.Windows, Env(("APPDATA", "C:/Users/me/AppData/Roaming")), "C:/Users/me");
        Assert.Equal(Path.Combine("C:/Users/me/AppData/Roaming", "MySSH.Desktop"), p.DataDirectory);
        Assert.Equal(Path.Combine("C:/Users/me/AppData/Roaming", "myssh"), p.LegacyDataDirectory);
        var known = AppPaths.Resolve(AppPlatform.Windows, Env(), "C:/Users/me", windowsRoamingAppData: "R:/Roam");
        Assert.Equal(Path.Combine("R:/Roam", "myssh"), known.LegacyDataDirectory);
    }

    [Fact]
    public void Linux_follows_xdg_data_home_like_rust_dirs()
    {
        var xdg = AppPaths.Resolve(AppPlatform.Linux, Env(("XDG_DATA_HOME", "/data/xdg")), "/home/me");
        Assert.Equal("/data/xdg/myssh-desktop", xdg.DataDirectory);
        Assert.Equal("/data/xdg/myssh", xdg.LegacyDataDirectory);

        var relative = AppPaths.Resolve(AppPlatform.Linux, Env(("XDG_DATA_HOME", "relative/dir")), "/home/me");
        Assert.Equal("/home/me/.local/share/myssh", relative.LegacyDataDirectory);

        var unset = AppPaths.Resolve(AppPlatform.Linux, Env(), "/home/me");
        Assert.Equal("/home/me/.local/share/myssh-desktop", unset.DataDirectory);
        Assert.Equal("/home/me/.local/share/myssh", unset.LegacyDataDirectory);
    }

    [Fact]
    public void Default_resolves_for_the_current_platform_without_collision()
    {
        var p = AppPaths.Default;
        Assert.False(string.Equals(p.DataDirectory, p.LegacyDataDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith(AppPaths.LegacyDirectoryName, p.LegacyDataDirectory, StringComparison.Ordinal);
        if (OperatingSystem.IsMacOS())
            Assert.EndsWith("Library/Application Support/MySSH.Desktop", p.DataDirectory, StringComparison.Ordinal);
        Assert.Same(AppPaths.Default, StorageOptions.Default.Paths);
    }

    [Fact]
    public async Task Open_refuses_a_data_directory_equal_to_the_legacy_one_case_insensitively()
    {
        using var dir = new TempDir();
        var options = T.Options(dir) with { Paths = new AppPaths { DataDirectory = dir.Sub("MySSH"), LegacyDataDirectory = dir.Sub("myssh") } };
        await Assert.ThrowsAsync<ArgumentException>(() => MySshStorage.OpenAsync(options, T.Ct));
    }
}
