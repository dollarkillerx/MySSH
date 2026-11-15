using Avalonia.Controls;

namespace MySSH.Carbon.Gallery.Pages;

public partial class SelectionPage : UserControl
{
    public SelectionPage()
    {
        InitializeComponent();
        DemoStates.Apply(this);
    }
}
