using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;

namespace MySSH.UI.Carbon;

/// <summary>
/// IBM Plex has no CJK glyphs. Left to <c>$Default</c>, Avalonia picks the OS-language font per character run: on a
/// Japanese macOS, Simplified Chinese UI text then mixes Hiragino Sans (Japanese) with Hiragino Sans GB for the
/// characters Japanese fonts lack — different weights in one word and a taller line box that clips labels. The first
/// installed CJK family in the chain wins, so it must match the UI language: Chinese UI → SC fonts first, Japanese UI →
/// JP fonts first, otherwise the first CJK language among the OS preferences.
/// </summary>
public static class CarbonFonts
{
    public const string PlexSans = "avares://MySSH.UI.Carbon/Assets/Fonts#IBM Plex Sans";
    public const string PlexMono = "avares://MySSH.UI.Carbon/Assets/Fonts#IBM Plex Mono";

    // macOS, Windows, Linux (Noto / Source Han / WenQuanYi) in that order; missing families are skipped.
    static readonly string[] s_simplifiedChinese =
        ["PingFang SC", "Hiragino Sans GB", "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC", "WenQuanYi Micro Hei"];
    static readonly string[] s_japanese =
        ["Hiragino Sans", "Hiragino Kaku Gothic ProN", "Yu Gothic UI", "Meiryo UI", "Meiryo", "Noto Sans CJK JP", "Noto Sans JP", "Source Han Sans JP"];

    /// <summary>Which CJK script to prefer for <paramref name="uiCulture"/>; non-CJK UIs follow the OS language order.</summary>
    public static bool PrefersJapanese(CultureInfo uiCulture, IEnumerable<string>? osLanguages = null)
    {
        ArgumentNullException.ThrowIfNull(uiCulture);
        if (IsJapanese(uiCulture.Name)) return true;
        if (uiCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return false;
        var firstCjk = (osLanguages ?? [CultureInfo.InstalledUICulture.Name])
            .FirstOrDefault(l => IsJapanese(l) || l.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        return firstCjk is not null && IsJapanese(firstCjk);
    }

    static bool IsJapanese(string name) => name.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

    static string[] Cjk(bool japanese) => japanese ? s_japanese : s_simplifiedChinese;

    public static FontFamily Sans(bool japanese) => new(string.Join(", ", [PlexSans, .. Cjk(japanese), "$Default"]));

    public static FontFamily Mono(bool japanese) => new(string.Join(", ", [PlexMono, "Menlo", "Consolas", "DejaVu Sans Mono", .. Cjk(japanese), "$Default"]));

    /// <summary>Overrides <c>CarbonFontSans</c> / <c>CarbonFontMono</c> (dynamic resources) in <paramref name="resources"/>.</summary>
    public static void Apply(IResourceDictionary resources, bool japanese)
    {
        ArgumentNullException.ThrowIfNull(resources);
        resources["CarbonFontSans"] = Sans(japanese);
        resources["ContentControlThemeFontFamily"] = resources["CarbonFontSans"];
        resources["CarbonFontMono"] = Mono(japanese);
    }
}
