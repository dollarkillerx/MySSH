using System.Security.Cryptography;
using System.Text.Json;
using MySSH.Core;

namespace MySSH.Storage;

public enum VaultState { Uninitialized, Locked, Unlocked }

/// <summary>How the MasterKey is protected at rest.</summary>
public enum VaultMode { None, OsKeystore, MasterPassword }

public enum VaultUnlockResult
{
    Unlocked,
    AlreadyUnlocked,
    NotInitialized,
    /// <summary>The vault uses the other protection mode (e.g. password given for an OS-keystore vault).</summary>
    WrongMode,
    WrongPassword,
    /// <summary>OS-keystore mode, but the keystore has no key for this vault.</summary>
    KeyNotFound,
    /// <summary>OS-keystore mode, the keystore returned a key that does not match this vault.</summary>
    KeyMismatch,
    KeystoreUnavailable,
}

/// <summary>
/// Holds the 32-byte MasterKey (design §5.1) and performs field encryption. The key is either kept in the OS keystore
/// (<see cref="IOsKeyStore"/>) or wrapped by an Argon2id-derived KEK from a master password. The small vault file stores
/// mode, KDF params, salt, wrapped key / key check — never the key itself.
/// </summary>
public sealed class Vault : IDisposable
{
    private const string KeyCheckAad = "vault:key-check:v1";
    private const string WrappedKeyAad = "vault:master-key:v1";
    private static readonly byte[] s_keyCheckPlaintext = "MySSH vault key check"u8.ToArray();

    private readonly Lock _gate = new();
    private readonly string _filePath;
    private readonly IOsKeyStore? _keyStore;
    private readonly Argon2Parameters _newKdfParameters;
    private VaultFileModel? _file;
    private byte[]? _masterKey;

    internal Vault(string filePath, IOsKeyStore? keyStore, Argon2Parameters newKdfParameters)
    {
        _filePath = filePath;
        _keyStore = keyStore;
        _newKdfParameters = newKdfParameters;
        _file = ReadFile(filePath);
    }

    public event EventHandler<VaultState>? StateChanged;

    public VaultState State
    {
        get
        {
            lock (_gate)
                return _file is null ? VaultState.Uninitialized : _masterKey is null ? VaultState.Locked : VaultState.Unlocked;
        }
    }

    public bool IsUnlocked => State == VaultState.Unlocked;

    public VaultMode Mode
    {
        get
        {
            lock (_gate)
                return _file?.Mode switch
                {
                    VaultFileModel.ModeOsKeystore => VaultMode.OsKeystore,
                    VaultFileModel.ModeMasterPassword => VaultMode.MasterPassword,
                    _ => VaultMode.None,
                };
        }
    }

    /// <summary>True when an OS keystore is available on this platform (false on Linux in P1).</summary>
    public bool IsOsKeystoreAvailable => _keyStore is { IsAvailable: true };

    /// <summary>Kind of the configured keystore ("macos-keychain", "windows-dpapi", ...), or null.</summary>
    public string? OsKeystoreKind => _keyStore?.Kind;

    /// <summary>Argon2id parameters persisted in the vault file (master-password mode), else null.</summary>
    public Argon2Parameters? KdfParameters
    {
        get
        {
            lock (_gate)
                return _file?.Kdf is { } k ? new Argon2Parameters(k.MemoryKiB, k.Iterations, k.Parallelism) : null;
        }
    }

    /// <summary>Error from the last automatic OS-keystore unlock attempt (set by <see cref="MySshStorage.OpenAsync"/>).</summary>
    public Exception? LastUnlockError { get; internal set; }

    public string FilePath => _filePath;

    /// <summary>
    /// Creates a vault protected by the OS keystore and leaves it unlocked. If the keystore already holds a 32-byte key
    /// for this service/account and <paramref name="adoptExistingKey"/> is true, that key is reused instead of being
    /// overwritten (a stray vault.json deletion must never destroy the key).
    /// </summary>
    public Task InitializeWithOsKeystoreAsync(bool adoptExistingKey = true, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var store = RequireKeyStore();
        EnsureUninitialized();
        byte[]? existing = adoptExistingKey ? store.Load() : null;
        var key = existing is { Length: FieldCipher.KeySize } ? existing : RandomNumberGenerator.GetBytes(FieldCipher.KeySize);
        if (!ReferenceEquals(key, existing))
            store.Save(key);
        var file = NewOsKeystoreFile(store, key);
        WriteFile(file);
        SetState(file, key);
        return Task.CompletedTask;
    }

    /// <summary>Creates a vault protected by a master password (Argon2id KEK wraps the MasterKey) and leaves it unlocked.</summary>
    public async Task InitializeWithMasterPasswordAsync(string masterPassword, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);
        EnsureUninitialized();
        var key = RandomNumberGenerator.GetBytes(FieldCipher.KeySize);
        var file = await NewMasterPasswordFileAsync(masterPassword, key, ct).ConfigureAwait(false);
        EnsureUninitialized();
        WriteFile(file);
        SetState(file, key);
    }

    /// <summary>Unlock an OS-keystore vault.</summary>
    public Task<VaultUnlockResult> UnlockAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        VaultFileModel? file;
        lock (_gate)
        {
            file = _file;
            if (file is null) return Task.FromResult(VaultUnlockResult.NotInitialized);
            if (_masterKey is not null) return Task.FromResult(VaultUnlockResult.AlreadyUnlocked);
        }
        if (file.Mode != VaultFileModel.ModeOsKeystore)
            return Task.FromResult(VaultUnlockResult.WrongMode);
        if (_keyStore is not { IsAvailable: true })
            return Task.FromResult(VaultUnlockResult.KeystoreUnavailable);

        var key = _keyStore.Load();
        if (key is null)
            return Task.FromResult(VaultUnlockResult.KeyNotFound);
        if (key.Length != FieldCipher.KeySize || !KeyMatches(file, key))
        {
            CryptographicOperations.ZeroMemory(key);
            return Task.FromResult(VaultUnlockResult.KeyMismatch);
        }
        SetState(file, key);
        return Task.FromResult(VaultUnlockResult.Unlocked);
    }

    /// <summary>Unlock a master-password vault. A wrong password is detected from the wrapped key's GCM tag; no data is touched.</summary>
    public async Task<VaultUnlockResult> UnlockAsync(string masterPassword, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);
        VaultFileModel? file;
        lock (_gate)
        {
            file = _file;
            if (file is null) return VaultUnlockResult.NotInitialized;
            if (_masterKey is not null) return VaultUnlockResult.AlreadyUnlocked;
        }
        if (file.Mode != VaultFileModel.ModeMasterPassword)
            return VaultUnlockResult.WrongMode;
        var key = await TryUnwrapAsync(file, masterPassword, ct).ConfigureAwait(false);
        if (key is null)
            return VaultUnlockResult.WrongPassword;
        SetState(file, key);
        return VaultUnlockResult.Unlocked;
    }

    /// <summary>Forget the MasterKey (zeroed). Non-secret data stays readable.</summary>
    public void Lock()
    {
        bool changed;
        lock (_gate)
        {
            changed = _masterKey is not null;
            if (_masterKey is not null)
                CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = null;
        }
        if (changed)
            StateChanged?.Invoke(this, VaultState.Locked);
    }

    /// <summary>Re-wraps the same MasterKey under a new password (and the currently configured Argon2 params). Data is untouched.</summary>
    public async Task ChangeMasterPasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        VaultFileModel file;
        lock (_gate)
            file = _file ?? throw new InvalidOperationException("The vault is not initialized.");
        if (file.Mode != VaultFileModel.ModeMasterPassword)
            throw new InvalidOperationException("The vault is not protected by a master password.");
        var key = await TryUnwrapAsync(file, currentPassword, ct).ConfigureAwait(false)
                  ?? throw new CryptographicException("The current master password is wrong.");
        try
        {
            var newFile = await NewMasterPasswordFileAsync(newPassword, key, ct).ConfigureAwait(false);
            newFile.CreatedAt = file.CreatedAt;
            WriteFile(newFile);
            lock (_gate)
                _file = newFile;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Switch an unlocked vault to master-password protection; the keystore entry is removed afterwards.</summary>
    public async Task SwitchToMasterPasswordAsync(string masterPassword, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);
        var (old, key) = SnapshotUnlocked();
        var newFile = await NewMasterPasswordFileAsync(masterPassword, key, ct).ConfigureAwait(false);
        newFile.CreatedAt = old.CreatedAt;
        WriteFile(newFile);
        lock (_gate)
            _file = newFile;
        CryptographicOperations.ZeroMemory(key);
        if (old.Mode == VaultFileModel.ModeOsKeystore && _keyStore is { IsAvailable: true })
            _keyStore.Delete();
    }

    /// <summary>Switch an unlocked vault to OS-keystore protection.</summary>
    public Task SwitchToOsKeystoreAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var store = RequireKeyStore();
        var (old, key) = SnapshotUnlocked();
        try
        {
            store.Save(key);
            var newFile = NewOsKeystoreFile(store, key);
            newFile.CreatedAt = old.CreatedAt;
            WriteFile(newFile);
            lock (_gate)
                _file = newFile;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return Task.CompletedTask;
    }

    public void Dispose() => Lock();

    // ---- field encryption (used by repositories) ----

    internal byte[] Encrypt(string table, Guid id, string field, string plaintext)
    {
        lock (_gate)
        {
            if (_masterKey is null) throw new VaultLockedException();
            return FieldCipher.EncryptString(_masterKey, plaintext, FieldCipher.Aad(table, id, field));
        }
    }

    /// <exception cref="VaultLockedException"/>
    /// <exception cref="CryptographicException">Blob does not decrypt with the current key and AAD.</exception>
    internal string Decrypt(string table, Guid id, string field, byte[] blob)
    {
        lock (_gate)
        {
            if (_masterKey is null) throw new VaultLockedException();
            return FieldCipher.DecryptString(_masterKey, blob, FieldCipher.Aad(table, id, field));
        }
    }

    internal void EnsureUnlocked()
    {
        if (!IsUnlocked) throw new VaultLockedException();
    }

    // ---- internals ----

    private IOsKeyStore RequireKeyStore() =>
        _keyStore is { IsAvailable: true } s
            ? s
            : throw new PlatformNotSupportedException("No OS keystore is available on this platform; use a master password.");

    private void EnsureUninitialized()
    {
        lock (_gate)
            if (_file is not null)
                throw new InvalidOperationException("The vault is already initialized.");
    }

    private (VaultFileModel File, byte[] KeyCopy) SnapshotUnlocked()
    {
        lock (_gate)
        {
            if (_file is null || _masterKey is null) throw new VaultLockedException();
            return (_file, (byte[])_masterKey.Clone());
        }
    }

    private void SetState(VaultFileModel file, byte[] key)
    {
        lock (_gate)
        {
            _file = file;
            if (_masterKey is not null) CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = key;
        }
        StateChanged?.Invoke(this, VaultState.Unlocked);
    }

    private static bool KeyMatches(VaultFileModel file, byte[] key)
    {
        if (file.KeyCheck is null) return false;
        try
        {
            var plain = FieldCipher.Decrypt(key, file.KeyCheck, KeyCheckAad);
            return CryptographicOperations.FixedTimeEquals(plain, s_keyCheckPlaintext);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static VaultFileModel NewOsKeystoreFile(IOsKeyStore store, byte[] key) => new()
    {
        Mode = VaultFileModel.ModeOsKeystore,
        Keystore = store.Kind,
        KeyCheck = FieldCipher.Encrypt(key, s_keyCheckPlaintext, KeyCheckAad),
    };

    private async Task<VaultFileModel> NewMasterPasswordFileAsync(string password, byte[] key, CancellationToken ct)
    {
        var p = _newKdfParameters;
        var salt = Argon2Kdf.NewSalt();
        var kek = await Argon2Kdf.DeriveKeyAsync(password, salt, p, ct).ConfigureAwait(false);
        try
        {
            return new VaultFileModel
            {
                Mode = VaultFileModel.ModeMasterPassword,
                Kdf = new VaultKdfModel { MemoryKiB = p.MemoryKiB, Iterations = p.Iterations, Parallelism = p.Parallelism, Salt = salt },
                WrappedKey = FieldCipher.Encrypt(kek, key, WrappedKeyAad),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static async Task<byte[]?> TryUnwrapAsync(VaultFileModel file, string password, CancellationToken ct)
    {
        if (file.Kdf is not { Salt: not null } kdf || file.WrappedKey is null)
            throw new InvalidDataException("The vault file is missing its KDF parameters or wrapped key.");
        if (!string.Equals(kdf.Algorithm, VaultKdfModel.Argon2id, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported vault KDF '{kdf.Algorithm}'.");
        var kek = await Argon2Kdf.DeriveKeyAsync(password, kdf.Salt, new Argon2Parameters(kdf.MemoryKiB, kdf.Iterations, kdf.Parallelism), ct).ConfigureAwait(false);
        try
        {
            var key = FieldCipher.Decrypt(kek, file.WrappedKey, WrappedKeyAad);
            return key.Length == FieldCipher.KeySize ? key : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static VaultFileModel? ReadFile(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var model = JsonSerializer.Deserialize(File.ReadAllBytes(path), StorageJsonContext.Default.VaultFileModel)
                        ?? throw new InvalidDataException("empty document");
            if (model.Version != VaultFileModel.CurrentVersion)
                throw new InvalidDataException($"unsupported vault file version {model.Version}");
            if (model.Mode is not (VaultFileModel.ModeOsKeystore or VaultFileModel.ModeMasterPassword))
                throw new InvalidDataException($"unknown vault mode '{model.Mode}'");
            return model;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The vault file '{path}' is corrupt and was left untouched: {e.Message}", e);
        }
        catch (InvalidDataException e) when (e.InnerException is null)
        {
            throw new InvalidDataException($"The vault file '{path}' is invalid and was left untouched: {e.Message}", e);
        }
    }

    private void WriteFile(VaultFileModel file)
    {
        file.UpdatedAt = DateTimeOffset.UtcNow;
        FileUtil.WriteAllBytesAtomic(_filePath, JsonSerializer.SerializeToUtf8Bytes(file, StorageJsonContext.Default.VaultFileModel));
    }
}

internal sealed class VaultFileModel
{
    public const int CurrentVersion = 1;
    public const string ModeOsKeystore = "os-keystore";
    public const string ModeMasterPassword = "master-password";

    public int Version { get; set; } = CurrentVersion;
    public string Mode { get; set; } = "";
    public string? Keystore { get; set; }
    /// <summary>OS-keystore mode: AES-GCM blob of a constant under the MasterKey, to detect a foreign key without touching data.</summary>
    public byte[]? KeyCheck { get; set; }
    public VaultKdfModel? Kdf { get; set; }
    /// <summary>Master-password mode: MasterKey encrypted with the Argon2id KEK (field-cipher blob).</summary>
    public byte[]? WrappedKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class VaultKdfModel
{
    public const string Argon2id = "argon2id";
    public string Algorithm { get; set; } = Argon2id;
    public int MemoryKiB { get; set; }
    public int Iterations { get; set; }
    public int Parallelism { get; set; }
    public byte[]? Salt { get; set; }
}
