using Avalonia.Controls;

namespace MySSH.Carbon.Gallery.Pages;

public partial class ListsPage : UserControl
{
    public ListsPage()
    {
        InitializeComponent();
        DemoStates.Apply(this);
    }
}
