using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Views;

public sealed partial class HostsPage : UserControl
{
    // DataGrid header sorting reflects over row properties (tools/aot/allowed-warnings.txt).
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.Interfaces, typeof(HostRowViewModel))]
    public HostsPage()
    {
        InitializeComponent();
        HostGrid.DoubleTapped += (_, _) => Vm?.ConnectCommand.Execute(null);
        HostGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Vm?.ConnectCommand.Execute(null); }
            else if (e.Key is Key.Delete or Key.Back && e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.Key == Key.Delete)
            { e.Handled = true; Vm?.DeleteHostCommand.Execute(null); }
        };
    }

    HostListViewModel? Vm => DataContext as HostListViewModel;

    void OnEditorSave(object? sender, RoutedEventArgs e) => Vm?.SaveEditorCommand.Execute(null);
    void OnEditorCancel(object? sender, RoutedEventArgs e) => Vm?.CancelEditorCommand.Execute(null);
    void OnErrorClosed(object? sender, RoutedEventArgs e) { if (Vm is not null) Vm.Error = null; }
}
