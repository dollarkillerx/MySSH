using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MySSH.UI.Carbon;

namespace MySSH.Carbon.Gallery.Pages;

public partial class IconsPage : UserControl
{
    public IconsPage()
    {
        InitializeComponent();
        foreach (var kind in CarbonIconNames.All)
        {
            var tile = new Border
            {
                Width = 120,
                Height = 88,
                Margin = new Thickness(0, 0, 1, 1),
                Classes = { "tile" },
                Child = new StackPanel
                {
                    Spacing = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new CarbonIcon { Kind = kind, Size = 20, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock
                        {
                            Text = kind,
                            Classes = { "label-01", "secondary" },
                            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    },
                },
            };
            Icons.Children.Add(tile);
        }

        foreach (var size in new[] { 16d, 20, 24, 32 })
        {
            Sizes.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 0, 32, 0),
                Spacing = 8,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new CarbonIcon { Kind = "settings", Size = size },
                            new CarbonIcon { Kind = "warning-filled", Size = size, InnerForeground = Avalonia.Media.Brushes.Black, Foreground = Avalonia.Media.Brush.Parse("#F1C21B") },
                            new CarbonIcon { Kind = "checkmark-filled", Size = size, Foreground = Avalonia.Media.Brush.Parse("#24A148") },
                        },
                    },
                    new TextBlock { Text = $"{size}px", Classes = { "label-01", "secondary" } },
                },
            });
        }
    }
}
