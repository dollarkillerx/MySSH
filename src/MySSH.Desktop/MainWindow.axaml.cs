using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using MySSH.Desktop.Localization;
using MySSH.Desktop.ViewModels;
using MySSH.UI.Carbon;

namespace MySSH.Desktop;

public sealed partial class MainWindow : Window
{
    ShellViewModel? _vm;

    // DataGrid header sorting reflects over row properties (see tools/aot/allowed-warnings.txt).
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.Interfaces, typeof(HostRowViewModel))]
    public MainWindow()
    {
        InitializeComponent();
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
        _vm.PropertyChanged += OnVmPropertyChanged;

        Nav.Items.Clear();
        foreach (var item in _vm.NavItems)
        {
            var label = new TextBlock();
            label.Bind(TextBlock.TextProperty, Loc.Instance.Bind(item.LabelKey));
            Nav.Items.Add(new SideNavItem { Icon = item.IconKind, Content = label, Tag = item });
        }
        Nav.SelectedIndex = 0;
        SyncModalsAndPage();
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsVaultPromptOpen) or nameof(ShellViewModel.IsLegacyOfferOpen)
            or nameof(ShellViewModel.IsCreatingMasterPassword) or nameof(ShellViewModel.SelectedNav))
            SyncModalsAndPage();
    }

    void SyncModalsAndPage()
    {
        if (_vm is null) return;
        VaultModal.IsOpen = _vm.IsVaultPromptOpen;
        LegacyModal.IsOpen = _vm.IsLegacyOfferOpen;
        if (_vm.IsVaultPromptOpen) MasterPasswordBox.Focus();

        var isHosts = _vm.SelectedNav?.Page is null or AppPage.Hosts;
        HostsPage.IsVisible = isHosts && _vm.Hosts is not null;
        LaterPage.IsVisible = !isHosts;
        if (!isHosts) LaterTitle.Text = Loc.Instance[_vm.SelectedNav!.LabelKey];
    }

    void OnLanguageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string culture }) Loc.Instance.SetCulture(culture);
        if (_vm?.SelectedNav is { Page: not AppPage.Hosts } nav) LaterTitle.Text = Loc.Instance[nav.LabelKey];
    }

    void OnNotificationClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: NotificationViewModel n }) _vm?.DismissNotificationCommand.Execute(n);
    }
}
