using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Views;

public sealed partial class IdentitiesPage : UserControl
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.Interfaces, typeof(IdentityRowViewModel))]
    public IdentitiesPage()
    {
        InitializeComponent();
        Grid.DoubleTapped += (_, _) => (DataContext as IdentitiesViewModel)?.EditCommand.Execute(null);
    }

    void OnErrorClosed(object? sender, RoutedEventArgs e) { if (DataContext is IdentitiesViewModel vm) vm.Error = null; }
}
