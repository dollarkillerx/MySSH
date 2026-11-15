using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.Storage;

namespace MySSH.Desktop.Tests;

/// <summary>P1 acceptance (design §12): start fresh, import the legacy Tauri data, show the host list.</summary>
[Collection("Loc")]
public sealed class AppSessionTests : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly string FixtureLegacyDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy", "appdata", "myssh");

    readonly string _root = Directory.CreateTempSubdirectory("myssh-desktop-tests-").FullName;
    AppPaths Paths => new() { DataDirectory = Path.Combine(_root, "MySSH.Desktop"), LegacyDataDirectory = Path.Combine(_root, "legacy", "myssh") };

    StorageOptions Options(IOsKeyStore? keyStore) => new()
    {
        Paths = Paths,
        OsKeyStore = keyStore,
        UsePlatformKeyStore = false,
        Argon2 = new Argon2Parameters(1024, 1, 1), // fast; Storage tests cover the real parameters
    };

    void CopyLegacyFixture()
    {
        Directory.CreateDirectory(Paths.LegacyDataDirectory);
        foreach (var f in Directory.GetFiles(FixtureLegacyDir))
            File.Copy(f, Path.Combine(Paths.LegacyDataDirectory, Path.GetFileName(f)));
    }

    [Fact]
    public async Task Fresh_install_with_keystore_imports_legacy_and_lists_hosts()
    {
        CopyLegacyFixture();
        var keyStore = new MemoryKeyStore();
        await using var session = await AppSession.OpenAsync(Options(keyStore), Ct);

        Assert.Equal(StartupPrompt.None, await session.PrepareVaultAsync(Ct));
        Assert.True(session.IsUnlocked);

        var offer = await session.DetectLegacyOfferAsync(Ct);
        Assert.NotNull(offer);
        var result = await session.ImportLegacyAsync(Ct);
        Assert.True(result.Hosts.Added > 0);
        Assert.True(result.FailedRecords >= 1); // the fixture contains one deliberately corrupted record

        Loc.Instance.SetCulture("en-US");
        var list = new HostListViewModel(session.Storage.Hosts, session.Storage.Identities, session.Storage.Proxies);
        await list.LoadAsync(Ct);
        Assert.Equal(result.Hosts.Added, list.Rows.Count);
        Assert.Contains(list.Rows, r => r.Via != "—");          // jump_host / proxy mapped
        Assert.All(list.Rows, r => Assert.Equal(AiPolicy.Off, r.AiPolicy)); // imported hosts invisible to AI by default

        Assert.Null(await session.DetectLegacyOfferAsync(Ct)); // offered once only
    }

    [Fact]
    public async Task Without_keystore_requires_master_password_and_rejects_wrong_one()
    {
        await using (var s = await AppSession.OpenAsync(Options(null), Ct))
        {
            Assert.Equal(StartupPrompt.CreateMasterPassword, await s.PrepareVaultAsync(Ct));
            await s.CreateMasterPasswordAsync("correct horse", Ct);
            Assert.True(s.IsUnlocked);
        }
        await using var again = await AppSession.OpenAsync(Options(null), Ct);
        Assert.Equal(StartupPrompt.UnlockMasterPassword, await again.PrepareVaultAsync(Ct));
        Assert.Equal(VaultUnlockResult.WrongPassword, await again.UnlockAsync("nope", Ct));
        Assert.False(again.IsUnlocked);
        Assert.Equal(VaultUnlockResult.Unlocked, await again.UnlockAsync("correct horse", Ct));
    }

    [Fact]
    public async Task Lost_keystore_key_is_reported_not_silently_reinitialised()
    {
        var keyStore = new MemoryKeyStore();
        await using (var s = await AppSession.OpenAsync(Options(keyStore), Ct))
            Assert.Equal(StartupPrompt.None, await s.PrepareVaultAsync(Ct));

        keyStore.Stored = null; // e.g. Keychain item deleted
        await using var again = await AppSession.OpenAsync(Options(keyStore), Ct);
        var ex = await Assert.ThrowsAsync<VaultUnavailableException>(() => again.PrepareVaultAsync(Ct));
        Assert.Equal(VaultUnlockResult.KeyNotFound, ex.Result);
        Assert.Null(keyStore.Stored); // nothing was overwritten
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    sealed class MemoryKeyStore : IOsKeyStore
    {
        public byte[]? Stored { get; set; }
        public string Kind => "test-memory";
        public bool IsAvailable => true;
        public byte[]? Load() => Stored?.ToArray();
        public void Save(ReadOnlySpan<byte> key) => Stored = key.ToArray();
        public void Delete() => Stored = null;
    }
}
