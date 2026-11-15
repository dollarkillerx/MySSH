using Avalonia.Controls;

namespace MySSH.Carbon.Gallery.Pages;

public partial class TabsPage : UserControl
{
    public TabsPage()
    {
        InitializeComponent();
        DemoStates.Apply(this);
    }
}
