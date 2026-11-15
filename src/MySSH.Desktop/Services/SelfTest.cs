using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Storage;

namespace MySSH.Desktop.Services;

/// <summary>
/// `MySSH --self-test &lt;legacyDir&gt;`: headless smoke test for NativeAOT builds (CI). Exercises SQLite, AES-GCM,
/// Argon2, source-generated JSON, the legacy importer and satellite resources in a throwaway directory,
/// using master-password mode so it never touches the OS keystore. Exit code 0 = pass.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string legacyDir)
    {
        var root = Directory.CreateTempSubdirectory("myssh-selftest-").FullName;
        try
        {
            var options = new StorageOptions
            {
                Paths = new AppPaths { DataDirectory = Path.Combine(root, AppPaths.DesktopDirectoryName), LegacyDataDirectory = Path.GetFullPath(legacyDir) },
                UsePlatformKeyStore = false,
                Argon2 = new Argon2Parameters(1024, 1, 1),
            };
            int hosts;
            await using (var session = await AppSession.OpenAsync(options))
            {
                Check(await session.PrepareVaultAsync() == StartupPrompt.CreateMasterPassword, "vault asks for a master password");
                await session.CreateMasterPasswordAsync("self-test");
                var offer = await session.DetectLegacyOfferAsync();
                Check(offer is not null, "legacy data detected");
                var result = await session.ImportLegacyAsync();
                hosts = (await session.Storage.Hosts.ListAsync()).Count;
                Check(hosts > 0 && hosts == result.Hosts.Added, $"imported {hosts} hosts");
                var withKey = (await session.Storage.Identities.ListAsync()).First(i => i.Kind == IdentityKind.PrivateKey);
                Check(await session.Storage.Identities.GetSecretsAsync(withKey.Id) is { PrivateKey.Length: > 0 }, "secrets decrypt");
            }
            await using (var reopened = await AppSession.OpenAsync(options))
            {
                Check(await reopened.PrepareVaultAsync() == StartupPrompt.UnlockMasterPassword, "reopen asks to unlock");
                Check(await reopened.UnlockAsync("self-test") == VaultUnlockResult.Unlocked, "unlock with master password");
                Check((await reopened.Storage.Hosts.ListAsync()).Count == hosts, "data persisted");
            }
            foreach (var (culture, expected) in new[] { ("en-US", "Hosts"), ("zh-CN", "主机"), ("ja-JP", "ホスト") })
            {
                Loc.Instance.SetCulture(culture);
                Check(Loc.Instance["nav.hosts"] == expected, $"resources {culture}");
            }
            Console.WriteLine("self-test: OK");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"self-test: FAILED: {e}");
            return 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    static void Check(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException("check failed: " + what);
        Console.WriteLine("  ok  " + what);
    }
}
