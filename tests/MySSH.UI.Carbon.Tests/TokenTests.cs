using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;

namespace MySSH.UI.Carbon.Tests;

public class TokenTests
{
    public static TheoryData<string, string, string> Expected => new()
    {
        // token, White (Light), Gray 100 (Dark)  -- design §9.1
        { "Background", "#ffffff", "#161616" },
        { "Layer01", "#f4f4f4", "#262626" },
        { "Layer02", "#ffffff", "#393939" },
        { "Field01", "#f4f4f4", "#262626" },
        { "BorderStrong01", "#8d8d8d", "#6f6f6f" },
        { "TextPrimary", "#161616", "#f4f4f4" },
        { "TextSecondary", "#525252", "#c6c6c6" },
        { "Interactive", "#0f62fe", "#4589ff" },
        { "Focus", "#0f62fe", "#ffffff" },
        { "ButtonPrimary", "#0f62fe", "#0f62fe" },
        { "ButtonPrimaryHover", "#0050e6", "#0050e6" },
        { "ButtonPrimaryActive", "#002d9c", "#002d9c" },
        { "SupportError", "#da1e28", "#fa4d56" },
        { "SupportSuccess", "#24a148", "#42be65" },
        { "SupportWarning", "#f1c21b", "#f1c21b" },
        { "SupportInfo", "#0043ce", "#4589ff" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Expected))]
    public void Semantic_tokens_match_Carbon_in_both_variants(string token, string light, string dark)
    {
        Assert.Equal(Color.Parse(light), Brush(token, ThemeVariant.Light));
        Assert.Equal(Color.Parse(dark), Brush(token, ThemeVariant.Dark));
        Assert.Equal(Color.Parse(light), ColorOf(token, ThemeVariant.Light));
        Assert.Equal(Color.Parse(dark), ColorOf(token, ThemeVariant.Dark));
    }

    public static TheoryData<string> AllTokens()
    {
        var data = new TheoryData<string>();
        foreach (var t in new[]
                 {
                     "Background", "Layer01", "Layer02", "Layer03", "LayerHover01", "LayerActive01", "LayerSelected01",
                     "LayerAccent01", "Field01", "Field02", "BorderSubtle00", "BorderSubtle01", "BorderSubtle02",
                     "BorderSubtle03", "BorderStrong01", "BorderInteractive", "TextPrimary", "TextSecondary",
                     "TextPlaceholder", "TextOnColor", "TextDisabled", "IconPrimary", "IconSecondary", "IconOnColor",
                     "Interactive", "Focus", "FocusInset", "LinkPrimary", "ButtonPrimary", "ButtonPrimaryHover",
                     "ButtonPrimaryActive", "ButtonSecondary", "ButtonSecondaryHover", "ButtonTertiary",
                     "ButtonDangerPrimary", "ButtonDangerHover", "ButtonDangerActive", "SupportError", "SupportSuccess",
                     "SupportWarning", "SupportInfo", "Highlight", "Overlay", "SkeletonBackground", "SkeletonElement",
                     "TagBackgroundBlue", "NotificationBackgroundError",
                 })
        {
            data.Add(t);
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(AllTokens))]
    public void Every_listed_token_resolves_in_all_four_Carbon_themes(string token)
    {
        foreach (var v in new[] { ThemeVariant.Light, ThemeVariant.Dark, CarbonThemeVariants.G10, CarbonThemeVariants.G90 })
        {
            Assert.True(Application.Current!.TryFindResource("Carbon" + token, v, out var b) && b is ISolidColorBrush,
                $"Carbon{token} missing for {v}");
        }
    }

    [AvaloniaFact]
    public void G10_and_G90_differ_from_White_and_Gray100()
    {
        Assert.Equal(Color.Parse("#f4f4f4"), Brush("Background", CarbonThemeVariants.G10));
        Assert.Equal(Color.Parse("#262626"), Brush("Background", CarbonThemeVariants.G90));
        Assert.Equal(Color.Parse("#ffffff"), Brush("Layer01", CarbonThemeVariants.G10));
        Assert.Equal(Color.Parse("#393939"), Brush("Layer01", CarbonThemeVariants.G90));
    }

    [AvaloniaFact]
    public void Spacing_type_and_motion_tokens_exist()
    {
        var app = Application.Current!;
        Assert.True(app.TryFindResource("CarbonSpacing05", out var s) && s is 16d);
        Assert.True(app.TryFindResource("CarbonSpacing13", out var s13) && s13 is 160d);
        Assert.True(app.TryFindResource("CarbonSpacing03Thickness", out var t) && t is Thickness { Left: 8 });
        Assert.True(app.TryFindResource("CarbonHeading03FontSize", out var fs) && fs is 20d);
        Assert.True(app.TryFindResource("CarbonBodyCompact01LineHeight", out var lh) && lh is 18d);
        Assert.True(app.TryFindResource("CarbonDurationFast02", out var d) && d is TimeSpan { TotalMilliseconds: 110 });
        Assert.True(app.TryFindResource("CarbonEasingStandardProductive", out var e) && e is Avalonia.Animation.Easings.SplineEasing);
        Assert.True(app.TryFindResource("CarbonFontSans", out var f) && f is FontFamily);
    }

    [AvaloniaFact]
    public void Simple_theme_compatibility_keys_are_recolored()
    {
        Assert.Equal(Color.Parse("#ffffff"), ColorOf2("ThemeBackgroundBrush", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#161616"), ColorOf2("ThemeBackgroundBrush", ThemeVariant.Dark));
        Assert.Equal(Color.Parse("#0f62fe"), ColorOf2("HighlightBrush", ThemeVariant.Light));
    }

    private static Color Brush(string token, ThemeVariant v) => ColorOf2("Carbon" + token, v);

    private static Color ColorOf2(string key, ThemeVariant v)
    {
        Assert.True(Application.Current!.TryFindResource(key, v, out var value), $"{key} not found for {v}");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Color ColorOf(string token, ThemeVariant v)
    {
        Assert.True(Application.Current!.TryFindResource("Carbon" + token + "Color", v, out var value));
        return Assert.IsType<Color>(value);
    }
}
