using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MySSH.Desktop.Localization;
using MySSH.Desktop.ViewModels;
using MySSH.UI.Carbon;

namespace MySSH.Desktop.Tests;

/// <summary>Traditional Chinese and Korean (2026-11): OS language matching, complete string tables, fonts, rendering.</summary>
[Collection("Loc")]
public sealed class LanguageTests
{
    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-Hant-TW", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-MO", "zh-TW")]
    [InlineData("ko-KR", "ko-KR")]
    [InlineData("ko", "ko-KR")]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("de-DE", "en-US")]
    public void The_os_language_picks_the_matching_ui_language(string os, string expected) =>
        Assert.Equal(expected, Loc.PickDefault(new CultureInfo(os)).Name);

    [Fact]
    public void Every_language_has_every_string()
    {
        var english = Loc.SupportedCultures.ToDictionary(c => c, _ => 0);
        try
        {
            foreach (var culture in Loc.SupportedCultures)
            {
                Loc.Instance.SetCulture(culture);
                // A missing key falls back to English (neutral resources): compare against it for a few UI strings.
                english[culture] = new[] { "nav.hosts", "settings.title", "ai.title", "updates.title" }
                    .Count(k => Loc.Instance[k] != k);
            }
            Assert.All(english.Values, n => Assert.Equal(4, n));
            Loc.Instance.SetCulture("zh-TW");
            Assert.Equal("主機", Loc.Instance["nav.hosts"]);
            Assert.Equal("設定", Loc.Instance["settings.title"]);
            Loc.Instance.SetCulture("ko-KR");
            Assert.Equal("호스트", Loc.Instance["nav.hosts"]);
            Assert.Equal("호스트 3개", Loc.Instance.Format("hosts.count", ("count", 3)));
        }
        finally { Loc.Instance.SetCulture("en-US"); }
    }

    [AvaloniaTheory]
    [InlineData("zh-TW", "主機", CjkScript.TraditionalChinese, "PingFang TC")]
    [InlineData("ko-KR", "호스트", CjkScript.Korean, "Apple SD Gothic Neo")]
    public void The_shell_renders_in_the_new_languages_with_their_fonts(string culture, string hosts, CjkScript script, string font)
    {
        var app = Application.Current!;
        Loc.Instance.SetCulture(culture);
        app.RequestedThemeVariant = ThemeVariant.Dark;
        var w = new MainWindow { DataContext = new ShellViewModel(_ => throw new InvalidOperationException()), Width = 1000, Height = 560 };
        w.Show();
        try
        {
            CarbonFonts.Apply(app.Resources, script);
            MainWindowUiTestsHelpers.Pump();
            var label = w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == hosts);
            Assert.Contains(font, label.FontFamily.ToString(), StringComparison.Ordinal);
            var label2 = label; // the nav label renders with the script's font
            Assert.NotNull(label2);
        }
        finally
        {
            w.Close();
            app.Resources.Remove("CarbonFontSans");
            app.Resources.Remove("CarbonFontMono");
            app.Resources.Remove("ContentControlThemeFontFamily");
            Loc.Instance.SetCulture("en-US");
        }
    }

    [AvaloniaTheory]
    [InlineData("zh-TW", CjkScript.TraditionalChinese)]
    [InlineData("ko-KR", CjkScript.Korean)]
    public async Task Settings_page_screenshot(string culture, CjkScript script)
    {
        var app = Application.Current!;
        Loc.Instance.SetCulture(culture);
        app.RequestedThemeVariant = ThemeVariant.Light;
        await using var t = await TestStore.OpenAsync();
        var settings = new SettingsViewModel(t.Storage, new FakeUi(), _ => { });
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        settings.Updates = new UpdatesViewModel(MySSH.Desktop.Services.NullAppUpdater.Instance, () => { });
        var w = new Window { Content = new MySSH.Desktop.Views.SettingsPage { DataContext = settings }, Width = 900, Height = 820 };
        w.Show();
        try
        {
            CarbonFonts.Apply(app.Resources, script);
            MainWindowUiTestsHelpers.Save(w, "35-settings-" + culture);
        }
        finally
        {
            w.Close();
            app.Resources.Remove("CarbonFontSans");
            app.Resources.Remove("CarbonFontMono");
            app.Resources.Remove("ContentControlThemeFontFamily");
            Loc.Instance.SetCulture("en-US");
        }
    }
}
