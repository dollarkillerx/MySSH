using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;

namespace MySSH.UI.Carbon;

/// <summary>The CJK script whose fonts come first after IBM Plex.</summary>
public enum CjkScript { SimplifiedChinese, TraditionalChinese, Japanese, Korean }

/// <summary>
/// IBM Plex has no CJK glyphs. Left to <c>$Default</c>, Avalonia picks the OS-language font per character run: on a
/// Japanese macOS, Simplified Chinese UI text then mixes Hiragino Sans (Japanese) with Hiragino Sans GB for the
/// characters Japanese fonts lack — different weights in one word and a taller line box that clips labels. The first
/// installed CJK family in the chain wins, so it must match the UI language (Traditional Chinese and Korean also need
/// their own families: the same code points have different glyph shapes per region), otherwise the first CJK language
/// among the OS preferences.
/// </summary>
public static class CarbonFonts
{
    public const string PlexSans = "avares://MySSH.UI.Carbon/Assets/Fonts#IBM Plex Sans";
    public const string PlexMono = "avares://MySSH.UI.Carbon/Assets/Fonts#IBM Plex Mono";

    // macOS, Windows, Linux (Noto / Source Han / others) in that order; missing families are skipped.
    static readonly string[] s_simplifiedChinese =
        ["PingFang SC", "Hiragino Sans GB", "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC", "WenQuanYi Micro Hei"];
    static readonly string[] s_traditionalChinese =
        ["PingFang TC", "PingFang HK", "Heiti TC", "Microsoft JhengHei UI", "Microsoft JhengHei", "Noto Sans CJK TC", "Noto Sans TC", "Source Han Sans TC"];
    static readonly string[] s_japanese =
        ["Hiragino Sans", "Hiragino Kaku Gothic ProN", "Yu Gothic UI", "Meiryo UI", "Meiryo", "Noto Sans CJK JP", "Noto Sans JP", "Source Han Sans JP"];
    static readonly string[] s_korean =
        ["Apple SD Gothic Neo", "Malgun Gothic", "Noto Sans CJK KR", "Noto Sans KR", "Source Han Sans KR", "NanumGothic"];

    /// <summary>The CJK script of a language tag ("zh-Hant-TW", "zh_HK", "ko", ...), or null for other languages.</summary>
    public static CjkScript? ScriptOf(string? language)
    {
        if (string.IsNullOrEmpty(language)) return null;
        var parts = language.Split('-', '_');
        return parts[0].ToUpperInvariant() switch
        {
            "JA" => CjkScript.Japanese,
            "KO" => CjkScript.Korean,
            "ZH" => parts.Skip(1).Any(p => p.Equals("Hant", StringComparison.OrdinalIgnoreCase) || p.ToUpperInvariant() is "TW" or "HK" or "MO")
                    && !parts.Skip(1).Any(p => p.Equals("Hans", StringComparison.OrdinalIgnoreCase))
                ? CjkScript.TraditionalChinese
                : CjkScript.SimplifiedChinese,
            _ => null,
        };
    }

    /// <summary>The UI language's script; for non-CJK UIs the first CJK language among the OS preferences.</summary>
    public static CjkScript PreferredScript(CultureInfo uiCulture, IEnumerable<string>? osLanguages = null)
    {
        ArgumentNullException.ThrowIfNull(uiCulture);
        return ScriptOf(uiCulture.Name)
            ?? (osLanguages ?? [CultureInfo.InstalledUICulture.Name]).Select(ScriptOf).FirstOrDefault(s => s is not null)
            ?? CjkScript.SimplifiedChinese;
    }

    static string[] Cjk(CjkScript script) => script switch
    {
        CjkScript.TraditionalChinese => s_traditionalChinese,
        CjkScript.Japanese => s_japanese,
        CjkScript.Korean => s_korean,
        _ => s_simplifiedChinese,
    };

    public static FontFamily Sans(CjkScript script) => new(string.Join(", ", [PlexSans, .. Cjk(script), "$Default"]));

    public static FontFamily Mono(CjkScript script) =>
        new(string.Join(", ", [PlexMono, "Menlo", "Consolas", "DejaVu Sans Mono", .. Cjk(script), "$Default"]));

    /// <summary>Overrides <c>CarbonFontSans</c> / <c>CarbonFontMono</c> (dynamic resources) in <paramref name="resources"/>.</summary>
    public static void Apply(IResourceDictionary resources, CjkScript script)
    {
        ArgumentNullException.ThrowIfNull(resources);
        resources["CarbonFontSans"] = Sans(script);
        resources["ContentControlThemeFontFamily"] = resources["CarbonFontSans"];
        resources["CarbonFontMono"] = Mono(script);
    }
}
