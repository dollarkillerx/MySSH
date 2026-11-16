using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using MySSH.UI.Carbon;

namespace MySSH.Desktop;

public sealed partial class MainWindow : Window, IUiServices
{
    ShellViewModel? _vm;

    // DataGrid header sorting reflects over row properties (see tools/aot/allowed-warnings.txt).
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.Interfaces, typeof(HostRowViewModel))]
    public MainWindow()
    {
        InitializeComponent();
        // The Carbon header doubles as the title bar on macOS (native traffic lights) and Windows (app-drawn caption
        // buttons, which only need the title bar part). On Linux the drawn decorations add a shadow and border that an
        // extended window has to inset itself (WindowDecorationMargin), so Linux keeps the window manager's title bar.
        ExtendClientAreaToDecorationsHint = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();
        // macOS uses the padded Apple-grid tile for the Dock (unbundled runs); elsewhere the full-bleed icon from XAML.
        if (OperatingSystem.IsMacOS())
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MySSH/Assets/icon-macos.png")));
        ThemeToggle.Click += (_, _) =>
        {
            var app = Application.Current!;
            app.RequestedThemeVariant = app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        };

        // Modals stay under view-model control: the primary button never auto-closes them,
        // and the vault prompt cannot be dismissed while the vault is still locked.
        VaultModal.PrimaryClick += (_, e) => { e.Handled = true; _vm?.SubmitMasterPasswordCommand.Execute(null); };
        VaultModal.Closed += (_, _) => { if (_vm?.IsVaultPromptOpen == true) VaultModal.IsOpen = true; };
        MasterPasswordBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; _vm?.SubmitMasterPasswordCommand.Execute(null); }
        };
        LegacyModal.PrimaryClick += (_, e) => { e.Handled = true; _vm?.ImportLegacyCommand.Execute(null); };
        LegacyModal.Closed += (_, _) => { if (_vm?.IsLegacyOfferOpen == true) _vm.DismissLegacyOfferCommand.Execute(null); };

        // Ctrl+Shift+A (Cmd+Shift+A on macOS) toggles the AI panel from anywhere, also while the terminal has focus.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            var mod = OperatingSystem.IsMacOS() ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control;
            if (e.Key == Avalonia.Input.Key.A && e.KeyModifiers == (mod | Avalonia.Input.KeyModifiers.Shift) && _vm?.Assistant is { } ai)
            {
                e.Handled = true;
                ai.ToggleCommand.Execute(null);
            }
        }, RoutingStrategies.Tunnel);

        Nav.SelectionChanged += (_, _) =>
        {
            if (_vm is not null && Nav.SelectedItem is SideNavItem { Tag: NavItemViewModel item }) _vm.SelectedNav = item;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as ShellViewModel;
        if (_vm is null) return;
        _vm.Ui = this;
        _vm.HostKeyVerifier = this;
        _vm.CredentialPrompt = this;
        _vm.Approvals = this;
        _vm.ApplySettings = ApplyTheme;
        _vm.PropertyChanged += OnVmPropertyChanged;

        Nav.Items.Clear();
        foreach (var item in _vm.NavItems)
        {
            // Plain (localized) text, not a TextBlock: the rail tooltip shows the same content, and a control cannot be in
            // two places (that crashed the app when hovering a collapsed rail icon).
            var navItem = new SideNavItem { Icon = item.IconKind, Tag = item };
            navItem.Bind(ContentControl.ContentProperty, Loc.Instance.Bind(item.LabelKey));
            Nav.Items.Add(navItem);
        }
        Nav.SelectedIndex = 0;
        SyncModals();
    }

    static void ApplyTheme(DesktopSettings s)
    {
        DesktopSettings.Current = s;
        Application.Current!.RequestedThemeVariant = s.Theme switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsVaultPromptOpen) or nameof(ShellViewModel.IsLegacyOfferOpen)
            or nameof(ShellViewModel.IsCreatingMasterPassword))
            SyncModals();
        else if (e.PropertyName == nameof(ShellViewModel.SelectedNav) && _vm?.SelectedNav is { } nav)
            Nav.SelectedItem = Nav.Items.OfType<SideNavItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, nav));
    }

    void SyncModals()
    {
        if (_vm is null) return;
        VaultModal.IsOpen = _vm.IsVaultPromptOpen;
        LegacyModal.IsOpen = _vm.IsLegacyOfferOpen;
        if (_vm.IsVaultPromptOpen) MasterPasswordBox.Focus();
    }

    async void OnLanguageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string culture }) return;
        Loc.Instance.SetCulture(culture);
        if (_vm?.Settings is { } settings) // persist like the Settings page does
        {
            await settings.LoadAsync();
            settings.Language = settings.Languages.FirstOrDefault(l => l.Culture == culture);
        }
    }

    void OnNotificationClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: NotificationViewModel n }) _vm?.DismissNotificationCommand.Execute(n);
    }

    // ---------------------------------------------------------------- IUiServices

    async Task<ModalResult> ShowDialogAsync(string heading, string body, string primary, bool danger, int passwordFields)
    {
        DialogModal.Heading = heading;
        DialogBody.Text = body;
        DialogBody.IsVisible = body.Length > 0;
        DialogModal.PrimaryButtonText = primary;
        DialogModal.SecondaryButtonText = Loc.Instance["common.cancel"];
        DialogModal.IsDanger = danger;
        DialogPassword.Text = DialogPasswordConfirm.Text = "";
        DialogPassword.IsVisible = passwordFields >= 1;
        DialogPasswordConfirm.IsVisible = passwordFields >= 2;
        Field.SetLabel(DialogPassword, Loc.Instance["settings.password"]);
        Field.SetLabel(DialogPasswordConfirm, Loc.Instance["settings.confirmPassword"]);
        Field.SetIsInvalid(DialogPasswordConfirm, false);

        var tcs = new TaskCompletionSource<ModalResult>();
        void OnPrimary(object? s, RoutedEventArgs e)
        {
            if (passwordFields >= 2 && DialogPassword.Text != DialogPasswordConfirm.Text)
            {
                e.Handled = true; // keep open
                Field.SetInvalidText(DialogPasswordConfirm, Loc.Instance["settings.passwordMismatch"]);
                Field.SetIsInvalid(DialogPasswordConfirm, true);
            }
        }
        void OnClosed(object? s, ModalClosedEventArgs e) => tcs.TrySetResult(e.Result);
        DialogModal.PrimaryClick += OnPrimary;
        DialogModal.Closed += OnClosed;
        DialogModal.IsOpen = true;
        if (passwordFields > 0) DialogPassword.Focus();
        else if (DialogText.IsVisible) { DialogText.Focus(); DialogText.SelectAll(); }
        try { return await tcs.Task; }
        finally
        {
            DialogModal.PrimaryClick -= OnPrimary;
            DialogModal.Closed -= OnClosed;
        }
    }

    public async Task<bool> ConfirmAsync(string heading, string body, string primaryText, bool danger) =>
        await ShowDialogAsync(heading, body, primaryText, danger, 0) == ModalResult.Primary;

    public async Task<string?> PromptPasswordAsync(string heading, string body, bool confirm)
    {
        var r = await ShowDialogAsync(heading, body, Loc.Instance["common.confirm"], false, confirm ? 2 : 1);
        var pw = DialogPassword.Text;
        DialogPassword.Text = DialogPasswordConfirm.Text = "";
        return r == ModalResult.Primary && !string.IsNullOrEmpty(pw) ? pw : null;
    }

    public async Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter>? filters = null)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters?.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList(),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter>? filters = null)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = filters?.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList(),
        });
        return file?.TryGetLocalPath();
    }

    public Task CopyToClipboardAsync(string text) => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    public async Task<string?> PromptTextAsync(string heading, string label, string initialValue)
    {
        DialogText.IsVisible = true;
        DialogText.Text = initialValue;
        Field.SetLabel(DialogText, label);
        try
        {
            var r = await ShowDialogAsync(heading, "", Loc.Instance["common.confirm"], false, 0);
            var text = DialogText.Text?.Trim();
            return r == ModalResult.Primary && !string.IsNullOrEmpty(text) ? text : null;
        }
        finally { DialogText.IsVisible = false; }
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task OpenUriAsync(Uri uri)
    {
        if (uri.Scheme is "http" or "https") await Launcher.LaunchUriAsync(uri);
    }
}
