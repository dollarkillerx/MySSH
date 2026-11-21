using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace MySSH.UI.Carbon.Tests;

/// <summary>
/// Regression: on a Japanese macOS, Simplified Chinese UI text mixed Hiragino Sans and Hiragino Sans GB (two weights in
/// one word, e.g. "我的服" regular next to "务器" bold). With CarbonFonts every CJK run uses one family.
/// </summary>
public sealed class CjkFontTests
{
    [Theory]
    [InlineData("zh-CN", null, CjkScript.SimplifiedChinese)]
    [InlineData("zh-TW", null, CjkScript.TraditionalChinese)]
    [InlineData("ja-JP", null, CjkScript.Japanese)]
    [InlineData("ko-KR", null, CjkScript.Korean)]
    [InlineData("en-US", new[] { "ja-JP", "zh-Hans-JP" }, CjkScript.Japanese)]
    [InlineData("en-US", new[] { "en-US", "zh-Hans-CN", "ja-JP" }, CjkScript.SimplifiedChinese)]
    [InlineData("en-US", new[] { "en-US", "zh-Hant-TW" }, CjkScript.TraditionalChinese)]
    [InlineData("en-US", new[] { "en-US", "zh-HK" }, CjkScript.TraditionalChinese)]
    [InlineData("en-US", new[] { "en-US", "ko-KR", "ja-JP" }, CjkScript.Korean)]
    [InlineData("en-US", new[] { "en-US", "de-DE" }, CjkScript.SimplifiedChinese)]
    public void Script_preference_follows_the_ui_language_then_the_os(string ui, string[]? os, CjkScript expected) =>
        Assert.Equal(expected, CarbonFonts.PreferredScript(new CultureInfo(ui), os ?? ["en-US"]));

    static List<(string Text, string Family)> CjkRuns(FontFamily family, string text)
    {
        var layout = new TextLayout(text, new Typeface(family, FontStyle.Normal, FontWeight.SemiBold), 14, Brushes.Black);
        return [.. layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>()
            .Select(r => (r.Text.ToString(), r.GlyphRun.GlyphTypeface.FamilyName))
            .Where(r => r.Item1.Any(c => c >= 0x3000))];
    }

    [AvaloniaTheory]
    [InlineData(CjkScript.SimplifiedChinese, "我的服务器 用户名 密钥与身份 新私钥 留空则使用身份中的用户名")]
    [InlineData(CjkScript.TraditionalChinese, "我的伺服器 使用者名稱 金鑰與身份 設定 主機")]
    [InlineData(CjkScript.Japanese, "ホスト 鍵と ID 既知のホスト 設定 パスワード")]
    [InlineData(CjkScript.Korean, "호스트 키 및 자격 증명 알려진 호스트 설정 비밀번호")]
    public void All_cjk_text_uses_one_family(CjkScript script, string text)
    {
        var family = CarbonFonts.Sans(script);
        var runs = CjkRuns(family, text);
        Assert.SkipWhen(runs.Count == 0 || runs.All(r => r.Family.Contains("Plex", StringComparison.Ordinal)), "no CJK system font installed");
        Assert.True(runs.Select(r => r.Family).Distinct().Count() == 1, string.Join(" ", runs.Select(r => $"{r.Text}={r.Family}")));
    }
}
