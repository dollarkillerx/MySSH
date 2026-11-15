using Avalonia.Controls;

namespace MySSH.Carbon.Gallery.Pages;

public partial class InputsPage : UserControl
{
    public InputsPage()
    {
        InitializeComponent();
        DemoStates.Apply(this);
    }
}
