using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using MySSH.UI.Carbon;

namespace MySSH.Carbon.Gallery;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        foreach (var page in GalleryPages.All)
        {
            Nav.Items.Add(new SideNavItem { Icon = page.Icon, Content = page.Title, Tag = page });
        }

        Nav.SelectionChanged += (_, _) =>
        {
            if (Nav.SelectedItem is SideNavItem { Tag: GalleryPage page })
            {
                Host.Content = page.Create();
                Scroller.Offset = default;
            }
        };
        Nav.SelectedIndex = 0;

        ThemeToggle.Click += (_, _) =>
        {
            if (Application.Current is { } app)
            {
                var dark = app.ActualThemeVariant == ThemeVariant.Dark || app.ActualThemeVariant == CarbonThemeVariants.G90;
                app.RequestedThemeVariant = dark ? ThemeVariant.Light : ThemeVariant.Dark;
                ButtonAssist.SetIcon(ThemeToggle, dark ? "asleep" : "light");
            }
        };
    }
}
