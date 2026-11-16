using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Views;

public sealed partial class AiMcpPage : UserControl
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.Interfaces, typeof(AuditRowViewModel))]
    public AiMcpPage() => InitializeComponent();
}
