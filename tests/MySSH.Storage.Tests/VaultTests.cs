using System.Security.Cryptography;
using System.Text.Json;
using MySSH.Core;

namespace MySSH.Storage.Tests;

public class VaultTests
{
    private static Identity PwIdentity(string label = "root") => new() { Label = label, Username = "root", Kind = IdentityKind.Password };

    [Fact]
    public async Task New_storage_has_uninitialized_vault_and_secret_access_throws()
    {
        using var dir = new TempDir();
        await using var s = await MySshStorage.OpenAsync(T.Options(dir, new InMemoryKeyStore()), T.Ct);
        Assert.Equal(VaultState.Uninitialized, s.Vault.State);
        Assert.Equal(VaultMode.None, s.Vault.Mode);
        Assert.True(s.Vault.IsOsKeystoreAvailable);
        var id = PwIdentity();
        await Assert.ThrowsAsync<VaultLockedException>(() => s.Identities.SaveAsync(id, new IdentitySecrets("pw", null, null), T.Ct));
        Assert.False(File.Exists(s.Paths.VaultFilePath));
    }

    [Fact]
    public async Task Os_keystore_vault_auto_unlocks_on_reopen()
    {
        using var dir = new TempDir();
        var ks = new InMemoryKeyStore();
        var id = PwIdentity();
        await using (var s = await T.OpenUnlockedAsync(dir, ks))
        {
            Assert.Equal(VaultMode.OsKeystore, s.Vault.Mode);
            Assert.Equal(32, ks.Stored!.Length);
            await s.Identities.SaveAsync(id, new IdentitySecrets("pw", null, null), T.Ct);
        }
        var vaultJson = await File.ReadAllTextAsync(dir.AppPaths.VaultFilePath, T.Ct);
        Assert.DoesNotContain(Convert.ToBase64String(ks.Stored!), vaultJson, StringComparison.Ordinal); // key never in the file

        await using (var s = await MySshStorage.OpenAsync(T.Options(dir, ks), T.Ct))
        {
            Assert.Equal(VaultState.Unlocked, s.Vault.State);
            Assert.Equal("pw", (await s.Identities.GetSecretsAsync(id.Id, T.Ct))!.Password);
        }
    }

    [Fact]
    public async Task Os_keystore_with_foreign_or_missing_key_stays_locked_without_touching_data()
    {
        using var dir = new TempDir();
        var ks = new InMemoryKeyStore();
        await using (var s = await T.OpenUnlockedAsync(dir, ks))
            await s.Identities.SaveAsync(PwIdentity(), new IdentitySecrets("pw", null, null), T.Ct);
        var vaultBytes = await File.ReadAllBytesAsync(dir.AppPaths.VaultFilePath, T.Ct);

        var foreign = new InMemoryKeyStore { Stored = RandomNumberGenerator.GetBytes(32) };
        await using (var s = await MySshStorage.OpenAsync(T.Options(dir, foreign), T.Ct))
        {
            Assert.Equal(VaultState.Locked, s.Vault.State);
            Assert.NotNull(s.Vault.LastUnlockError);
            Assert.Equal(VaultUnlockResult.KeyMismatch, await s.Vault.UnlockAsync(T.Ct));
            Assert.Single(await s.Identities.ListAsync(T.Ct)); // metadata still readable
        }

        await using (var s = await MySshStorage.OpenAsync(T.Options(dir, new InMemoryKeyStore()), T.Ct))
            Assert.Equal(VaultUnlockResult.KeyNotFound, await s.Vault.UnlockAsync(T.Ct));

        Assert.Equal(vaultBytes, await File.ReadAllBytesAsync(dir.AppPaths.VaultFilePath, T.Ct));
        Assert.Equal(1, ks.SaveCount);
    }

    [Fact]
    public async Task Os_keystore_initialization_adopts_an_existing_key_instead_of_overwriting_it()
    {
        using var dir = new TempDir();
        var existing = RandomNumberGenerator.GetBytes(32);
        var ks = new InMemoryKeyStore { Stored = (byte[])existing.Clone() };
        await using var s = await T.OpenUnlockedAsync(dir, ks);
        Assert.Equal(existing, ks.Stored);
        Assert.Equal(0, ks.SaveCount);
    }

    [Fact]
    public async Task Master_password_wrong_password_is_detected_without_touching_data()
    {
        using var dir = new TempDir();
        var id = PwIdentity();
        await using (var s = await MySshStorage.OpenAsync(T.Options(dir), T.Ct))
        {
            Assert.False(s.Vault.IsOsKeystoreAvailable);
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => s.Vault.InitializeWithOsKeystoreAsync(ct: T.Ct));
            await s.Vault.InitializeWithMasterPasswordAsync("correct horse", T.Ct);
            Assert.Equal(VaultMode.MasterPassword, s.Vault.Mode);
            await s.Identities.SaveAsync(id, new IdentitySecrets("pw", null, null), T.Ct);
        }

        var vaultBefore = await File.ReadAllBytesAsync(dir.AppPaths.VaultFilePath, T.Ct);
        await using (var s = await MySshStorage.OpenAsync(T.Options(dir), T.Ct))
        {
            Assert.Equal(VaultState.Locked, s.Vault.State);
            var blobBefore = (byte[])(await T.ScalarAsync(s, "SELECT password FROM identities;"))!;

            Assert.Equal(VaultUnlockResult.WrongPassword, await s.Vault.UnlockAsync("battery staple", T.Ct));
            Assert.Equal(VaultUnlockResult.WrongMode, await s.Vault.UnlockAsync(T.Ct));
            Assert.Equal(VaultState.Locked, s.Vault.State);
            await Assert.ThrowsAsync<VaultLockedException>(() => s.Identities.GetSecretsAsync(id.Id, T.Ct));

            Assert.Equal(blobBefore, (byte[])(await T.ScalarAsync(s, "SELECT password FROM identities;"))!);
            Assert.Empty(await s.UndecryptableRecords.ListAsync(ct: T.Ct));

            Assert.Equal(VaultUnlockResult.Unlocked, await s.Vault.UnlockAsync("correct horse", T.Ct));
            Assert.Equal("pw", (await s.Identities.GetSecretsAsync(id.Id, T.Ct))!.Password);
        }
        Assert.Equal(vaultBefore, await File.ReadAllBytesAsync(dir.AppPaths.VaultFilePath, T.Ct));
    }

    [Fact]
    public async Task Locked_vault_blocks_secrets_but_not_metadata()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var id = PwIdentity();
        var proxy = new ProxyProfile { Label = "p", Hostname = "proxy", Port = 1080, Kind = ProxyKind.Socks5 };
        await s.Identities.SaveAsync(id, new IdentitySecrets("pw", null, null), T.Ct);
        await s.Proxies.SaveAsync(proxy, "ppw", T.Ct);
        var states = new List<VaultState>();
        s.Vault.StateChanged += (_, st) => states.Add(st);

        s.Vault.Lock();
        Assert.Equal([VaultState.Locked], states);
        await Assert.ThrowsAsync<VaultLockedException>(() => s.Identities.GetSecretsAsync(id.Id, T.Ct));
        await Assert.ThrowsAsync<VaultLockedException>(() => s.Identities.SaveAsync(id, new IdentitySecrets("new", null, null), T.Ct));
        await Assert.ThrowsAsync<VaultLockedException>(() => s.Proxies.GetPasswordAsync(proxy.Id, T.Ct));
        await Assert.ThrowsAsync<VaultLockedException>(() => s.Backup.ExportAsync(new MemoryStream(), "pw", ct: T.Ct));
        await Assert.ThrowsAsync<VaultLockedException>(() => s.LegacyImport.ImportAsync(T.Ct));
        await Assert.ThrowsAsync<VaultLockedException>(() => s.UndecryptableRecords.ScanAsync(T.Ct));

        // Metadata edits keep the encrypted secret byte-for-byte.
        var blob = (byte[])(await T.ScalarAsync(s, "SELECT password FROM identities;"))!;
        await s.Identities.SaveAsync(id with { Label = "renamed" }, null, T.Ct);
        await s.Proxies.SaveAsync(proxy with { Port = 1081 }, null, T.Ct);
        Assert.Equal("renamed", (await s.Identities.GetAsync(id.Id, T.Ct))!.Label);
        Assert.Equal(blob, (byte[])(await T.ScalarAsync(s, "SELECT password FROM identities;"))!);
        Assert.True((await s.Proxies.GetAsync(proxy.Id, T.Ct))!.HasPassword);

        Assert.Equal(VaultUnlockResult.Unlocked, await s.Vault.UnlockAsync(T.Ct));
        Assert.Equal("pw", (await s.Identities.GetSecretsAsync(id.Id, T.Ct))!.Password);
        Assert.Equal("ppw", await s.Proxies.GetPasswordAsync(proxy.Id, T.Ct));
    }

    [Fact]
    public async Task Argon2_parameters_and_salt_are_persisted_and_used_for_unlock()
    {
        using var dir = new TempDir();
        var custom = new Argon2Parameters(2048, 2, 1);
        await using (var s = await MySshStorage.OpenAsync(T.Options(dir, argon: custom), T.Ct))
            await s.Vault.InitializeWithMasterPasswordAsync("pw", T.Ct);

        using (var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(dir.AppPaths.VaultFilePath, T.Ct)))
        {
            var kdf = doc.RootElement.GetProperty("kdf");
            Assert.Equal("argon2id", kdf.GetProperty("algorithm").GetString());
            Assert.Equal(2048, kdf.GetProperty("memoryKiB").GetInt32());
            Assert.Equal(2, kdf.GetProperty("iterations").GetInt32());
            Assert.Equal(1, kdf.GetProperty("parallelism").GetInt32());
            Assert.Equal(16, Convert.FromBase64String(kdf.GetProperty("salt").GetString()!).Length);
            Assert.Equal("master-password", doc.RootElement.GetProperty("mode").GetString());
        }

        // Reopen with different "new" params: the persisted ones are still used.
        await using (var s = await MySshStorage.OpenAsync(T.Options(dir, argon: new Argon2Parameters(4096, 1, 2)), T.Ct))
        {
            Assert.Equal(custom, s.Vault.KdfParameters);
            Assert.Equal(VaultUnlockResult.Unlocked, await s.Vault.UnlockAsync("pw", T.Ct));
        }
    }

    [Fact]
    public async Task Default_argon2_parameters_are_used_and_persisted()
    {
        using var dir = new TempDir();
        await using (var s = await MySshStorage.OpenAsync(T.Options(dir, argon: Argon2Parameters.Default), T.Ct))
            await s.Vault.InitializeWithMasterPasswordAsync("pw", T.Ct);
        await using (var s = await MySshStorage.OpenAsync(T.Options(dir), T.Ct))
        {
            Assert.Equal(new Argon2Parameters(65536, 3, 1), s.Vault.KdfParameters);
            Assert.Equal(VaultUnlockResult.WrongPassword, await s.Vault.UnlockAsync("nope", T.Ct));
            Assert.Equal(VaultUnlockResult.Unlocked, await s.Vault.UnlockAsync("pw", T.Ct));
        }
    }

    [Fact]
    public async Task Change_password_and_switch_modes_keep_the_same_master_key()
    {
        using var dir = new TempDir();
        var ks = new InMemoryKeyStore();
        var id = PwIdentity();
        await using (var s = await MySshStorage.OpenAsync(T.Options(dir, ks), T.Ct))
        {
            await s.Vault.InitializeWithMasterPasswordAsync("one", T.Ct);
            await s.Identities.SaveAsync(id, new IdentitySecrets("pw", null, null), T.Ct);
            await Assert.ThrowsAnyAsync<CryptographicException>(() => s.Vault.ChangeMasterPasswordAsync("wrong", "two", T.Ct));
            await s.Vault.ChangeMasterPasswordAsync("one", "two", T.Ct);
        }
        await using (var s = await MySshStorage.OpenAsync(T.Options(dir, ks), T.Ct))
        {
            Assert.Equal(VaultUnlockResult.WrongPassword, await s.Vault.UnlockAsync("one", T.Ct));
            Assert.Equal(VaultUnlockResult.Unlocked, await s.Vault.UnlockAsync("two", T.Ct));
            await s.Vault.SwitchToOsKeystoreAsync(T.Ct);
            Assert.NotNull(ks.Stored);
        }
        await using (var s = await MySshStorage.OpenAsync(T.Options(dir, ks), T.Ct))
        {
            Assert.Equal(VaultMode.OsKeystore, s.Vault.Mode);
            Assert.True(s.Vault.IsUnlocked);
            Assert.Equal("pw", (await s.Identities.GetSecretsAsync(id.Id, T.Ct))!.Password);
            await s.Vault.SwitchToMasterPasswordAsync("three", T.Ct);
            Assert.Null(ks.Stored); // keystore entry removed after switching away
        }
        await using (var s = await MySshStorage.OpenAsync(T.Options(dir, ks), T.Ct))
        {
            Assert.Equal(VaultUnlockResult.Unlocked, await s.Vault.UnlockAsync("three", T.Ct));
            Assert.Equal("pw", (await s.Identities.GetSecretsAsync(id.Id, T.Ct))!.Password);
        }
    }

    [Fact]
    public async Task Corrupt_vault_file_fails_loudly_and_is_left_untouched()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.AppPaths.DataDirectory);
        await File.WriteAllTextAsync(dir.AppPaths.VaultFilePath, "{ not json", T.Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => MySshStorage.OpenAsync(T.Options(dir, new InMemoryKeyStore()), T.Ct));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(dir.AppPaths.VaultFilePath, T.Ct));
    }

    [Fact]
    public async Task Initializing_twice_is_rejected()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Vault.InitializeWithMasterPasswordAsync("x", T.Ct));
    }
}
