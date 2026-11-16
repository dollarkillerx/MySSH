namespace MySSH.Desktop.Services;

/// <summary>Headless default: confirms nothing, picks nothing. Used by tests and before the window exists.</summary>
public sealed class NullUiServices : IUiServices
{
    public static readonly NullUiServices Instance = new();
    public Task<bool> ConfirmAsync(string heading, string body, string primaryText, bool danger) => Task.FromResult(false);
    public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter>? filters = null) => Task.FromResult<string?>(null);
    public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter>? filters = null) => Task.FromResult<string?>(null);
    public Task<string?> PromptPasswordAsync(string heading, string body, bool confirm) => Task.FromResult<string?>(null);
    public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
    public Task<string?> PromptTextAsync(string heading, string label, string initialValue) => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task OpenUriAsync(Uri uri) => Task.CompletedTask;
}
