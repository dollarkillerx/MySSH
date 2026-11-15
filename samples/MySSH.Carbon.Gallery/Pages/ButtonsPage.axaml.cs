using Avalonia.Controls;

namespace MySSH.Carbon.Gallery.Pages;

public partial class ButtonsPage : UserControl
{
    public ButtonsPage()
    {
        InitializeComponent();
        DemoStates.Apply(this);
    }
}
