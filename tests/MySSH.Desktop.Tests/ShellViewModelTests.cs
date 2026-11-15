using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.Storage;

namespace MySSH.Desktop.Tests;

[Collection("Loc")]
public sealed class ShellViewModelTests : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    readonly string _root = Directory.CreateTempSubdirectory("myssh-shell-tests-").FullName;

    StorageOptions Options(IOsKeyStore? keyStore) => new()
    {
        Paths = new AppPaths { DataDirectory = Path.Combine(_root, "MySSH.Desktop"), LegacyDataDirectory = Path.Combine(_root, "legacy", "myssh") },
        OsKeyStore = keyStore,
        UsePlatformKeyStore = false,
        Argon2 = new Argon2Parameters(1024, 1, 1),
    };

    static void CopyLegacyFixture(StorageOptions o)
    {
        var src = Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy", "appdata", "myssh");
        Directory.CreateDirectory(o.Paths.LegacyDataDirectory);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(o.Paths.LegacyDataDirectory, Path.GetFileName(f)));
    }

    [Fact]
    public async Task Startup_offers_legacy_import_then_imports_and_reports()
    {
        Loc.Instance.SetCulture("en-US");
        var o = Options(new KeyStore());
        CopyLegacyFixture(o);
        await using var shell = new ShellViewModel(ct => AppSession.OpenAsync(o, ct));
        await shell.InitializeAsync(Ct);

        Assert.False(shell.IsVaultPromptOpen);
        Assert.True(shell.IsLegacyOfferOpen);
        Assert.NotNull(shell.Hosts);
        Assert.Empty(shell.Hosts!.Rows);
        Assert.Contains(shell.LegacyOfferCount!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), shell.LegacyOfferText);

        await shell.ImportLegacyCommand.ExecuteAsync(null);

        Assert.False(shell.IsLegacyOfferOpen);
        Assert.NotEmpty(shell.Hosts.Rows);
        Assert.Contains(shell.Notifications, n => n.Kind == NotificationKind.Success && n.Message.StartsWith("Imported ", StringComparison.Ordinal));
        Assert.Contains(shell.Notifications, n => n.Kind == NotificationKind.Warning); // the corrupted fixture record
    }

    [Fact]
    public async Task Master_password_prompt_flow()
    {
        Loc.Instance.SetCulture("en-US");
        var o = Options(null);
        await using (var first = new ShellViewModel(ct => AppSession.OpenAsync(o, ct)))
        {
            await first.InitializeAsync(Ct);
            Assert.True(first.IsCreatingMasterPassword);
            first.MasterPassword = "s3cret";
            await first.SubmitMasterPasswordCommand.ExecuteAsync(null);
            Assert.False(first.IsVaultPromptOpen);
            Assert.Equal("", first.MasterPassword); // not kept around
        }

        await using var second = new ShellViewModel(ct => AppSession.OpenAsync(o, ct));
        await second.InitializeAsync(Ct);
        Assert.Equal(StartupPrompt.UnlockMasterPassword, second.VaultPrompt);
        second.MasterPassword = "wrong";
        await second.SubmitMasterPasswordCommand.ExecuteAsync(null);
        Assert.Equal("Wrong master password.", second.VaultError);
        Assert.True(second.IsVaultPromptOpen);
        Assert.Null(second.Hosts);
        second.MasterPassword = "s3cret";
        await second.SubmitMasterPasswordCommand.ExecuteAsync(null);
        Assert.False(second.IsVaultPromptOpen);
        Assert.NotNull(second.Hosts);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    sealed class KeyStore : IOsKeyStore
    {
        byte[]? _k;
        public string Kind => "test-memory";
        public bool IsAvailable => true;
        public byte[]? Load() => _k?.ToArray();
        public void Save(ReadOnlySpan<byte> key) => _k = key.ToArray();
        public void Delete() => _k = null;
    }
}
