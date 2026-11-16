using MySSH.Core;
using MySSH.Desktop.Services;
using MySSH.Storage;

namespace MySSH.Desktop.Tests;

/// <summary>A real, unlocked MySshStorage in a temp dir (in-memory keystore, cheap Argon2).</summary>
sealed class TestStore : IAsyncDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("myssh-p2-tests-").FullName;
    public MySshStorage Storage { get; private set; } = null!;

    public static async Task<TestStore> OpenAsync()
    {
        var t = new TestStore();
        t.Storage = await MySshStorage.OpenAsync(new StorageOptions
        {
            Paths = new AppPaths { DataDirectory = Path.Combine(t._root, "MySSH.Desktop"), LegacyDataDirectory = Path.Combine(t._root, "legacy", "myssh") },
            OsKeyStore = new MemKeyStore(),
            UsePlatformKeyStore = false,
            Argon2 = new Argon2Parameters(1024, 1, 1),
        });
        await t.Storage.Vault.InitializeWithOsKeystoreAsync();
        return t;
    }

    public async ValueTask DisposeAsync()
    {
        await Storage.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    sealed class MemKeyStore : IOsKeyStore
    {
        byte[]? _k;
        public string Kind => "test-memory";
        public bool IsAvailable => true;
        public byte[]? Load() => _k?.ToArray();
        public void Save(ReadOnlySpan<byte> key) => _k = key.ToArray();
        public void Delete() => _k = null;
    }
}

/// <summary>Scriptable dialogs.</summary>
public sealed class FakeUi : IUiServices
{
    public bool ConfirmResult { get; set; } = true;
    public string? Password { get; set; }
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }
    public List<string> Confirms { get; } = [];
    public string? Clipboard { get; private set; }
    public Task<bool> ConfirmAsync(string heading, string body, string primaryText, bool danger) { Confirms.Add(body); return Task.FromResult(ConfirmResult); }
    public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter>? filters = null) => Task.FromResult(OpenPath);
    public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter>? filters = null) => Task.FromResult(SavePath);
    public Task<string?> PromptPasswordAsync(string heading, string body, bool confirm) => Task.FromResult(Password);
    public Task CopyToClipboardAsync(string text) { Clipboard = text; return Task.CompletedTask; }
    public Queue<string?> TextAnswers { get; } = new();
    public string? Folder { get; set; }
    public List<Uri> Opened { get; } = [];
    public Task<string?> PromptTextAsync(string heading, string label, string initialValue) => Task.FromResult(TextAnswers.Count > 0 ? TextAnswers.Dequeue() : null);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult(Folder);
    public Task OpenUriAsync(Uri uri) { Opened.Add(uri); return Task.CompletedTask; }
}
