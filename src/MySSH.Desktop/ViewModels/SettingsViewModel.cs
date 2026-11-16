using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Storage;

namespace MySSH.Desktop.ViewModels;

public sealed record LanguageOption(string? Culture, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Language, theme, backup export/import and data-store information (design §5).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    readonly MySshStorage _storage;
    readonly IUiServices _ui;
    readonly Action<DesktopSettings> _apply;
    DesktopSettings _settings = new();
    bool _loading;

    public SettingsViewModel(MySshStorage storage, IUiServices ui, Action<DesktopSettings> apply)
    {
        _storage = storage;
        _ui = ui;
        _apply = apply;
    }

    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(null, Loc.Instance["settings.languageAuto"]),
        new("en-US", "English"),
        new("zh-CN", "简体中文"),
        new("ja-JP", "日本語"),
    ];
    public IReadOnlyList<ThemePreference> Themes { get; } = [ThemePreference.System, ThemePreference.Light, ThemePreference.Dark];

    [ObservableProperty] public partial LanguageOption? Language { get; set; }
    [ObservableProperty] public partial ThemePreference Theme { get; set; }
    [ObservableProperty] public partial bool ConfirmMultilinePaste { get; set; } = true;
    [ObservableProperty] public partial bool CheckForUpdates { get; set; } = true;
    /// <summary>Set by the shell (one instance for the app's lifetime).</summary>
    [ObservableProperty] public partial UpdatesViewModel? Updates { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    public string DataDirectory => _storage.Paths.DataDirectory;
    public string VaultModeText => _storage.Vault.Mode switch
    {
        VaultMode.OsKeystore => Loc.Instance.Format("settings.vaultOs", ("kind", _storage.Vault.OsKeystoreKind ?? "?")),
        VaultMode.MasterPassword => Loc.Instance["settings.vaultPassword"],
        _ => "—",
    };

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _loading = true;
        _settings = await DesktopSettings.LoadAsync(_storage.Settings, ct);
        Language = Languages.FirstOrDefault(l => l.Culture == _settings.Language) ?? Languages[0];
        Theme = _settings.Theme;
        ConfirmMultilinePaste = _settings.ConfirmMultilinePaste;
        CheckForUpdates = _settings.CheckForUpdates;
        _loading = false;
    }

    partial void OnLanguageChanged(LanguageOption? value) => _ = PersistAsync();
    partial void OnThemeChanged(ThemePreference value) => _ = PersistAsync();
    partial void OnConfirmMultilinePasteChanged(bool value) => _ = PersistAsync();
    partial void OnCheckForUpdatesChanged(bool value) => _ = PersistAsync();

    async Task PersistAsync()
    {
        if (_loading) return;
        _settings = _settings with { Language = Language?.Culture, Theme = Theme, ConfirmMultilinePaste = ConfirmMultilinePaste,
            CheckForUpdates = CheckForUpdates };
        _apply(_settings);
        await _settings.SaveAsync(_storage.Settings);
    }

    [RelayCommand]
    async Task ExportBackupAsync()
    {
        Message = null;
        var password = await _ui.PromptPasswordAsync(Loc.Instance["settings.exportTitle"], Loc.Instance["settings.backupHint"], confirm: true);
        if (password is null) return;
        if (password.Length < 8) { Show(Loc.Instance["settings.passwordTooShort"], error: true); return; }
        var path = await _ui.PickSaveFileAsync(Loc.Instance["settings.saveBackup"], $"myssh-backup-{DateTime.Now:yyyyMMdd}.mysshbk",
            [new FileFilter("MySSH backup", ["*.mysshbk"])]);
        if (path is null) return;
        try
        {
            await using var fs = File.Create(path);
            var r = await _storage.Backup.ExportAsync(fs, password);
            Show(Loc.Instance.Format("settings.exportDone", ("count", r.Hosts)), error: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BackupException or VaultLockedException)
        {
            Show(Loc.Instance.Format("settings.exportFailed", ("error", e.Message)), error: true);
        }
    }

    [RelayCommand]
    async Task ImportBackupAsync()
    {
        Message = null;
        var path = await _ui.PickOpenFileAsync(Loc.Instance["settings.selectBackupFile"]);
        if (path is null) return;
        var password = await _ui.PromptPasswordAsync(Loc.Instance["settings.importTitle"], Loc.Instance["settings.enterPassword"], confirm: false);
        if (password is null) return;
        try
        {
            await using var fs = File.OpenRead(path);
            var r = await _storage.Backup.ImportAsync(fs, password);
            var text = Loc.Instance.Format("settings.importSuccess", ("count", r.Hosts.Added + r.Hosts.Updated));
            if (r.IsWeakLegacyFormat) text += " " + Loc.Instance["settings.weakBackup"];
            Show(text, error: false);
            BackupImported?.Invoke();
        }
        catch (BackupException e) when (e.Error == BackupError.WrongPasswordOrCorrupt)
        {
            Show(Loc.Instance["settings.wrongBackupPassword"], error: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BackupException or VaultLockedException)
        {
            Show(Loc.Instance.Format("settings.importFailed", ("error", e.Message)), error: true);
        }
    }

    /// <summary>Raised after a successful import so other pages can reload.</summary>
    public event Action? BackupImported;

    void Show(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
    }
}
