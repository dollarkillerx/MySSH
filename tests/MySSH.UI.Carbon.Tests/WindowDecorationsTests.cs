using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace MySSH.UI.Carbon.Tests;

/// <summary>
/// Windows / Linux draw the caption buttons from the theme's WindowDrawnDecorations template. They sit on the always-dark
/// ShellHeader, so their glyphs must stay light in the light theme (Simple's follow the theme: invisible on the header).
/// </summary>
public sealed class WindowDecorationsTests
{
    static ControlTheme Theme()
    {
        Assert.True(Application.Current!.TryFindResource(typeof(WindowDrawnDecorations), out var t));
        return Assert.IsType<ControlTheme>(t);
    }

    static object? Setter(ControlTheme theme, AvaloniaProperty p) =>
        theme.Setters.OfType<Setter>().Single(s => s.Property == p).Value;

    [AvaloniaFact]
    public void Carbon_draws_the_decorations_at_header_height()
    {
        var theme = Theme();
        Assert.Equal(48d, Setter(theme, WindowDrawnDecorations.DefaultTitleBarHeightProperty));
        var template = Assert.IsAssignableFrom<IWindowDrawnDecorationsTemplate>(Setter(theme, WindowDrawnDecorations.TemplateProperty));
        var content = template.Build().Result;
        var overlay = Assert.IsAssignableFrom<Control>(content.Overlay);
        var names = overlay.GetLogicalDescendants().OfType<Button>().Select(b => b.Name).ToList();
        Assert.Equal(["PART_FullScreenButton", "PART_MinimizeButton", "PART_MaximizeButton", "PART_CloseButton"], names);
        // No title text over the header (the header shows the product name).
        Assert.Empty(overlay.GetLogicalDescendants().OfType<TextBlock>());
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Caption_glyphs_stay_light_in_both_themes(string variant)
    {
        var template = (IWindowDrawnDecorationsTemplate)Setter(Theme(), WindowDrawnDecorations.TemplateProperty)!;
        var overlay = (Control)template.Build().Result.Overlay!;
        var window = new Window
        {
            RequestedThemeVariant = variant == "Light" ? ThemeVariant.Light : ThemeVariant.Dark,
            Content = overlay, Width = 400, Height = 100,
        };
        window.Show();
        var paths = overlay.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().ToList();
        Assert.Equal(4, paths.Count);
        foreach (var path in paths)
            Assert.Equal(Color.Parse("#f4f4f4"), Assert.IsAssignableFrom<ISolidColorBrush>(path.Fill).Color);
        var close = overlay.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "PART_CloseButton");
        Assert.Equal(46, close.Bounds.Width);
        Assert.Equal(48, close.Bounds.Height);
        window.Close();
    }
}
