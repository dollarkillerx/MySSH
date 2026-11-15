using Avalonia.Styling;

namespace MySSH.UI.Carbon;

/// <summary>
/// Carbon theme variants. <see cref="ThemeVariant.Light"/> maps to Carbon White and
/// <see cref="ThemeVariant.Dark"/> to Carbon Gray 100; these add the Gray 10 and Gray 90 themes.
/// </summary>
public static class CarbonThemeVariants
{
    /// <summary>Carbon White (same as <see cref="ThemeVariant.Light"/>).</summary>
    public static ThemeVariant White => ThemeVariant.Light;

    /// <summary>Carbon Gray 10, a light theme (falls back to Light resources).</summary>
    public static ThemeVariant G10 { get; } = new("G10", ThemeVariant.Light);

    /// <summary>Carbon Gray 90, a dark theme (falls back to Dark resources).</summary>
    public static ThemeVariant G90 { get; } = new("G90", ThemeVariant.Dark);

    /// <summary>Carbon Gray 100 (same as <see cref="ThemeVariant.Dark"/>).</summary>
    public static ThemeVariant G100 => ThemeVariant.Dark;
}
