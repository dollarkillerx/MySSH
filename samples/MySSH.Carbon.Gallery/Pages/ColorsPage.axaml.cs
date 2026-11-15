using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using MySSH.UI.Carbon;

namespace MySSH.Carbon.Gallery.Pages;

public partial class ColorsPage : UserControl
{
    internal static readonly string[] Tokens =
    [
        "Background", "BackgroundHover", "BackgroundSelected", "BackgroundInverse", "BackgroundBrand",
        "Layer01", "Layer02", "Layer03", "LayerHover01", "LayerActive01", "LayerSelected01", "LayerAccent01",
        "Field01", "Field02", "FieldHover01",
        "BorderSubtle00", "BorderSubtle01", "BorderSubtle02", "BorderSubtle03", "BorderStrong01", "BorderInteractive",
        "TextPrimary", "TextSecondary", "TextPlaceholder", "TextHelper", "TextOnColor", "TextDisabled", "TextError",
        "IconPrimary", "IconSecondary", "IconDisabled",
        "Interactive", "Focus", "FocusInset", "LinkPrimary", "Highlight",
        "ButtonPrimary", "ButtonPrimaryHover", "ButtonPrimaryActive", "ButtonSecondary", "ButtonTertiary", "ButtonDangerPrimary", "ButtonDisabled",
        "SupportError", "SupportSuccess", "SupportWarning", "SupportInfo",
        "Overlay", "SkeletonBackground", "SkeletonElement", "ToggleOff",
    ];

    public ColorsPage()
    {
        InitializeComponent();
        foreach (var token in Tokens)
        {
            var swatch = new Border
            {
                Height = 48,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            swatch.Bind(Border.BackgroundProperty, swatch.GetResourceObservable("Carbon" + token));
            var hex = new TextBlock { Classes = { "code-01", "secondary" } };
            hex.Bind(TextBlock.TextProperty, hex.GetResourceObservable("Carbon" + token + "Color", c => c is Color col ? col.ToString() : "?"));
            Swatches.Children.Add(new StackPanel
            {
                Width = 160,
                Margin = new Thickness(0, 0, 16, 16),
                Spacing = 4,
                Children = { swatch, new TextBlock { Text = token, Classes = { "label-01" } }, hex },
            });
        }

        Variant.SelectionChanged += (_, _) =>
        {
            if (Application.Current is { } app)
            {
                app.RequestedThemeVariant = Variant.SelectedIndex switch
                {
                    1 => CarbonThemeVariants.White,
                    2 => CarbonThemeVariants.G10,
                    3 => CarbonThemeVariants.G90,
                    4 => CarbonThemeVariants.G100,
                    _ => ThemeVariant.Default,
                };
            }
        };
    }
}
