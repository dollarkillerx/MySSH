using MySSH.Storage;

namespace MySSH.Desktop.Services;

/// <summary>What the shell must ask the user before the app is usable.</summary>
public enum StartupPrompt
{
    None,
    /// <summary>Vault exists in master-password mode and is locked.</summary>
    UnlockMasterPassword,
    /// <summary>No OS keystore (Linux, or keystore unavailable): a master password must be created.</summary>
    CreateMasterPassword,
}

/// <summary>
/// Owns the open <see cref="MySshStorage"/> and the startup sequence (design §5):
/// open store → initialise / unlock vault → (UI) offer legacy import → scan for undecryptable records.
/// UI-agnostic so it can be tested without Avalonia.
/// </summary>
public sealed class AppSession : IAsyncDisposable
{
    AppSession(MySshStorage storage) => Storage = storage;

    public MySshStorage Storage { get; }
    public bool IsUnlocked => Storage.Vault.IsUnlocked;

    public static async Task<AppSession> OpenAsync(StorageOptions options, CancellationToken ct = default) =>
        new(await MySshStorage.OpenAsync(options, ct));

    /// <summary>
    /// Brings the vault to Unlocked when that needs no user input; otherwise says which prompt to show.
    /// A fresh install with an OS keystore is initialised silently (design §5.1 default).
    /// </summary>
    public async Task<StartupPrompt> PrepareVaultAsync(CancellationToken ct = default)
    {
        var vault = Storage.Vault;
        switch (vault.State)
        {
            case VaultState.Unlocked:
                return StartupPrompt.None;
            case VaultState.Uninitialized when vault.IsOsKeystoreAvailable:
                await vault.InitializeWithOsKeystoreAsync(adoptExistingKey: true, ct);
                return vault.IsUnlocked ? StartupPrompt.None : StartupPrompt.CreateMasterPassword;
            case VaultState.Uninitialized:
                return StartupPrompt.CreateMasterPassword;
            default: // Locked
                if (vault.Mode == VaultMode.MasterPassword) return StartupPrompt.UnlockMasterPassword;
                var r = await vault.UnlockAsync(ct);
                if (r is VaultUnlockResult.Unlocked or VaultUnlockResult.AlreadyUnlocked) return StartupPrompt.None;
                // OS keystore lost / foreign key: data cannot be decrypted with it. Surface it; never re-initialise
                // over existing encrypted data.
                throw new VaultUnavailableException(r, vault.LastUnlockError);
        }
    }

    public Task<VaultUnlockResult> UnlockAsync(string masterPassword, CancellationToken ct = default) =>
        Storage.Vault.UnlockAsync(masterPassword, ct);

    public Task CreateMasterPasswordAsync(string masterPassword, CancellationToken ct = default) =>
        Storage.Vault.InitializeWithMasterPasswordAsync(masterPassword, ct);

    /// <summary>Legacy (Tauri) data worth offering: present, readable, and not imported before.</summary>
    public async Task<LegacyDetection?> DetectLegacyOfferAsync(CancellationToken ct = default)
    {
        var d = await Storage.LegacyImport.DetectAsync(ct);
        return d is { CanImport: true, AlreadyImported: false, RecordCount: > 0 } ? d : null;
    }

    public Task<ImportResult> ImportLegacyAsync(CancellationToken ct = default) => Storage.LegacyImport.ImportAsync(ct);

    public async Task<int> CountUndecryptableAsync(CancellationToken ct = default) =>
        (await Storage.UndecryptableRecords.ScanAsync(ct)).Count;

    public ValueTask DisposeAsync() => Storage.DisposeAsync();
}

public sealed class VaultUnavailableException(VaultUnlockResult result, Exception? inner)
    : InvalidOperationException($"The vault could not be unlocked from the OS keystore ({result}).", inner)
{
    public VaultUnlockResult Result { get; } = result;
}
