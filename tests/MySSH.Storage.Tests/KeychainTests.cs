using System.Security.Cryptography;

namespace MySSH.Storage.Tests;

/// <summary>
/// Touches the REAL macOS login keychain, so it only runs with MYSSH_TEST_KEYCHAIN=1. Uses a unique throwaway service
/// name and deletes the item afterwards.
/// </summary>
public class KeychainTests
{
    [Fact]
    public async Task Keychain_store_round_trip_and_vault_auto_unlock()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MYSSH_TEST_KEYCHAIN") == "1", "Set MYSSH_TEST_KEYCHAIN=1 to run against the real macOS Keychain.");
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only.");
        if (!OperatingSystem.IsMacOS()) return;

        var service = $"MySSH.Tests.{Guid.NewGuid():N}";
        var store = new MacKeychainKeyStore(service, "master-key") { Label = "MySSH test item (safe to delete)", AllowInteraction = false };
        try
        {
            Assert.Null(store.Load());
            var key = RandomNumberGenerator.GetBytes(32);
            store.Save(key);
            Assert.Equal(key, store.Load());
            var key2 = RandomNumberGenerator.GetBytes(32);
            store.Save(key2); // update path
            Assert.Equal(key2, store.Load());
            store.Delete();
            Assert.Null(store.Load());

            using var dir = new TempDir();
            await using (var s = await MySshStorage.OpenAsync(T.Options(dir, store), T.Ct))
            {
                await s.Vault.InitializeWithOsKeystoreAsync(ct: T.Ct);
                await s.Identities.SaveAsync(new MySSH.Core.Identity { Label = "k", Kind = MySSH.Core.IdentityKind.Password }, new MySSH.Core.IdentitySecrets("pw", null, null), T.Ct);
            }
            await using (var s = await MySshStorage.OpenAsync(T.Options(dir, store), T.Ct))
            {
                Assert.Null(s.Vault.LastUnlockError);
                Assert.True(s.Vault.IsUnlocked);
                var id = Assert.Single(await s.Identities.ListAsync(T.Ct));
                Assert.Equal("pw", (await s.Identities.GetSecretsAsync(id.Id, T.Ct))!.Password);
            }
        }
        finally
        {
            store.Delete();
            Assert.Null(store.Load());
        }
    }
}
