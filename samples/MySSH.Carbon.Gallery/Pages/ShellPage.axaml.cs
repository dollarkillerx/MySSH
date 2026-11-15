using Avalonia.Controls;

namespace MySSH.Carbon.Gallery.Pages;

public partial class ShellPage : UserControl
{
    public ShellPage()
    {
        InitializeComponent();
        DemoStates.Apply(this);
    }
}
