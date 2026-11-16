using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Views;

public sealed partial class KnownHostsPage : UserControl
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.Interfaces, typeof(KnownHostRowViewModel))]
    public KnownHostsPage() => InitializeComponent();
}
