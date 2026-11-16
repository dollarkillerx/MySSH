namespace MySSH.Desktop.Services;

public sealed record FileFilter(string Name, IReadOnlyList<string> Patterns);

/// <summary>Dialogs and pickers behind an interface so view models stay testable without a window.</summary>
public interface IUiServices
{
    Task<bool> ConfirmAsync(string heading, string body, string primaryText, bool danger);
    Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter>? filters = null);
    Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter>? filters = null);
    /// <summary>Asks for a password (twice when <paramref name="confirm"/>). null = cancelled.</summary>
    Task<string?> PromptPasswordAsync(string heading, string body, bool confirm);
    Task CopyToClipboardAsync(string text);
    /// <summary>Single-line text input (new folder name, rename…). null = cancelled.</summary>
    Task<string?> PromptTextAsync(string heading, string label, string initialValue);
    Task<string?> PickFolderAsync(string title);
    /// <summary>Opens a URL in the system browser (terminal links).</summary>
    Task OpenUriAsync(Uri uri);
}
