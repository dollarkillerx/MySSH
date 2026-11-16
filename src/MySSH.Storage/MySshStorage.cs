using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySSH.Core;

namespace MySSH.Storage;

public sealed record StorageOptions
{
    public required AppPaths Paths { get; init; }

    /// <summary>
    /// Keystore for the OS-keystore vault mode. Null = platform default (<see cref="OsKeyStores.CreateDefault"/>:
    /// Keychain on macOS, DPAPI file on Windows, none on Linux).
    /// </summary>
    public IOsKeyStore? OsKeyStore { get; init; }

    /// <summary>When true (default) a platform default keystore is used if <see cref="OsKeyStore"/> is null. Tests set false.</summary>
    public bool UsePlatformKeyStore { get; init; } = true;

    /// <summary>Argon2id parameters for NEW wraps (master password, backups). Existing files keep their persisted params.</summary>
    public Argon2Parameters Argon2 { get; init; } = Argon2Parameters.Default;

    /// <summary>Try to unlock an OS-keystore vault during <see cref="MySshStorage.OpenAsync"/> (failures are recorded, not thrown).</summary>
    public bool AutoUnlockOsKeystore { get; init; } = true;

    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>Per-user default locations (<see cref="AppPaths.Default"/>).</summary>
    public static StorageOptions Default => new() { Paths = AppPaths.Default };
}

/// <summary>
/// Entry point for the Desktop shell: opens (creating/migrating) the database in <see cref="AppPaths.DataDirectory"/>,
/// loads the vault and exposes the repositories.
/// <para>Vault flow: if <c>Vault.State == Uninitialized</c>, ask the user and call
/// <see cref="Vault.InitializeWithOsKeystoreAsync"/> (when <see cref="Vault.IsOsKeystoreAvailable"/>) or
/// <see cref="Vault.InitializeWithMasterPasswordAsync"/>. If <c>Locked</c> in master-password mode, prompt and call
/// <see cref="Vault.UnlockAsync(string, CancellationToken)"/>. Non-secret data (host list etc.) is readable while locked.</para>
/// </summary>
public sealed class MySshStorage : IAsyncDisposable
{
    private readonly StorageDatabase _db;
    private bool _disposed;

    private MySshStorage(StorageOptions options, StorageDatabase db, Vault vault, int schemaVersion)
    {
        Options = options;
        _db = db;
        Vault = vault;
        SchemaVersion = schemaVersion;
        var logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("MySSH.Storage");
        var settings = new SettingsStore(db);
        Settings = settings;
        Hosts = new HostRepository(db);
        Identities = new IdentityRepository(db, vault);
        Proxies = new ProxyRepository(db, vault);
        Groups = new GroupRepository(db);
        KnownHosts = new KnownHostStore(db);
        AuditLog = new AuditLogStore(db);
        Secrets = new SecretSettingsStore(db, vault);
        AiConversations = new AiConversationStore(db, vault);
        UndecryptableRecords = new UndecryptableRecordStore(db, vault);
        LegacyImport = new LegacyImportService(options.Paths.LegacyDataDirectory, db, vault, settings, logger);
        Backup = new BackupService(db, vault, options.Argon2);
    }

    public StorageOptions Options { get; }
    public AppPaths Paths => Options.Paths;
    public int SchemaVersion { get; }

    public IHostRepository Hosts { get; }
    public IIdentityRepository Identities { get; }
    public IProxyRepository Proxies { get; }
    public IGroupRepository Groups { get; }
    public IKnownHostStore KnownHosts { get; }
    public ISettingsStore Settings { get; }
    /// <summary>Tool-call audit log (design §6.1). Readable while the vault is locked.</summary>
    public IAuditLogStore AuditLog { get; }
    /// <summary>Vault-encrypted named secrets (MCP token, ...). Requires an unlocked vault; never included in backups.</summary>
    public ISecretSettingsStore Secrets { get; }
    public IAiConversationStore AiConversations { get; }
    public Vault Vault { get; }
    public LegacyImportService LegacyImport { get; }
    public BackupService Backup { get; }
    /// <summary>Secrets that failed to decrypt (kept and flagged, never dropped).</summary>
    public UndecryptableRecordStore UndecryptableRecords { get; }

    public static async Task<MySshStorage> OpenAsync(StorageOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Argon2.Validate();
        var paths = options.Paths;
        if (string.Equals(Path.GetFullPath(paths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(paths.LegacyDataDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The data directory must differ from the legacy data directory (case-insensitively).", nameof(options));

        FileUtil.EnsurePrivateDirectory(paths.DataDirectory);
        var db = new StorageDatabase(paths.DatabasePath);
        var version = await db.InitializeAsync(ct).ConfigureAwait(false);

        var keyStore = options.OsKeyStore ?? (options.UsePlatformKeyStore ? OsKeyStores.CreateDefault(paths) : null);
        var vault = new Vault(paths.VaultFilePath, keyStore, options.Argon2);
        if (options.AutoUnlockOsKeystore && vault.State == VaultState.Locked && vault.Mode == VaultMode.OsKeystore)
        {
            try
            {
                var r = await vault.UnlockAsync(ct).ConfigureAwait(false);
                if (r != VaultUnlockResult.Unlocked)
                    vault.LastUnlockError = new InvalidOperationException($"Automatic vault unlock failed: {r}.");
            }
            catch (OsKeyStoreException e)
            {
                vault.LastUnlockError = e;
            }
        }
        return new MySshStorage(options, db, vault, version);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        Vault.Dispose();
        _db.ClearPool();
        return ValueTask.CompletedTask;
    }
}
