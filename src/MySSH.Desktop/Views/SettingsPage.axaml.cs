using Avalonia.Controls;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Views;

public sealed partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SettingsViewModel.MessageIsError))
                        MessageBar.Kind = vm.MessageIsError ? MySSH.UI.Carbon.NotificationKind.Error : MySSH.UI.Carbon.NotificationKind.Success;
                };
        };
        MessageBar.Closed += (_, _) => { if (DataContext is SettingsViewModel vm) vm.Message = null; };
    }
}
