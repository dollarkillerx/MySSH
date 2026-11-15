using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MySSH.Core;

namespace MySSH.Storage.Tests;

/// <summary>Throwaway directory under the system temp dir; deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "myssh-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public AppPaths AppPaths => new() { DataDirectory = Sub("MySSH.Desktop"), LegacyDataDirectory = Sub("myssh") };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Default test keystore: keeps the MasterKey in memory (never touches the real OS keystore).</summary>
public sealed class InMemoryKeyStore : IOsKeyStore
{
    public byte[]? Stored { get; set; }
    public int SaveCount { get; private set; }
    public string Kind => "test-memory";
    public bool IsAvailable => true;
    public byte[]? Load() => Stored is null ? null : (byte[])Stored.Clone();

    public void Save(ReadOnlySpan<byte> key)
    {
        Stored = key.ToArray();
        SaveCount++;
    }

    public void Delete() => Stored = null;
}

internal static class T
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Cheap Argon2 parameters so tests stay fast; a dedicated test covers <see cref="Argon2Parameters.Default"/>.</summary>
    public static readonly Argon2Parameters FastArgon2 = new(1024, 1, 1);

    public static StorageOptions Options(TempDir dir, IOsKeyStore? keyStore = null, Argon2Parameters? argon = null, string? legacyDir = null) => new()
    {
        Paths = legacyDir is null ? dir.AppPaths : dir.AppPaths with { LegacyDataDirectory = legacyDir },
        OsKeyStore = keyStore,
        UsePlatformKeyStore = false,
        Argon2 = argon ?? FastArgon2,
    };

    /// <summary>Opens storage with an in-memory keystore and an initialized, unlocked vault.</summary>
    public static async Task<MySshStorage> OpenUnlockedAsync(TempDir dir, InMemoryKeyStore? keyStore = null, string? legacyDir = null)
    {
        var storage = await MySshStorage.OpenAsync(Options(dir, keyStore ?? new InMemoryKeyStore(), legacyDir: legacyDir), Ct);
        if (storage.Vault.State == VaultState.Uninitialized)
            await storage.Vault.InitializeWithOsKeystoreAsync(ct: Ct);
        Assert.True(storage.Vault.IsUnlocked);
        return storage;
    }

    public static async Task<SqliteConnection> RawConnectionAsync(MySshStorage storage)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = storage.Paths.DatabasePath, Pooling = false }.ToString());
        await conn.OpenAsync(Ct);
        return conn;
    }

    public static async Task<object?> ScalarAsync(MySshStorage storage, string sql, params (string, object?)[] ps)
    {
        await using var conn = await RawConnectionAsync(storage);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps)
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = await cmd.ExecuteScalarAsync(Ct);
        return r is DBNull ? null : r;
    }

    public static async Task ExecAsync(MySshStorage storage, string sql, params (string, object?)[] ps)
    {
        await using var conn = await RawConnectionAsync(storage);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps)
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MySSH.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root (MySSH.slnx) not found.");
        }
    }

    public static string FixturesDir => Path.Combine(RepoRoot, "tests", "fixtures", "legacy");

    public static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    }

    public static string HashDirectory(string dir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
        {
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(f)));
            sha.AppendData(File.ReadAllBytes(f));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    public static Host NewHost(string label, string hostname = "example.com") => new() { Label = label, Hostname = hostname };
}
