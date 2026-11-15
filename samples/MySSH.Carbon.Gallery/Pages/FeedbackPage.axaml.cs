using Avalonia.Controls;
using MySSH.UI.Carbon;

namespace MySSH.Carbon.Gallery.Pages;

public partial class FeedbackPage : UserControl
{
    public FeedbackPage()
    {
        InitializeComponent();
        foreach (var type in Enum.GetValues<TagType>())
        {
            TagTypes.Children.Add(new Tag { Type = type, Content = type.ToString() });
        }

        DemoStates.Apply(this);
    }
}
