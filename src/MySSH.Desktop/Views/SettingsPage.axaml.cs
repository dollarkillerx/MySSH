using Avalonia.Controls;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Views;

public sealed partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        // The bar's kind is bound (SettingsViewModel.MessageKind). The page is rebuilt on every visit while the view
        // model lives for the whole app, so the page must not subscribe to it: each visit used to leak a page.
        MessageBar.Closed += (_, _) => { if (DataContext is SettingsViewModel vm) vm.Message = null; };
    }
}
