using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MySSH.Desktop.Localization;
using MySSH.Desktop.ViewModels;
using MySSH.UI.Carbon;

namespace MySSH.Desktop.Tests;

[Collection("Loc")]
public sealed class FontResourceTests
{
    [AvaloniaFact]
    public void App_level_font_chain_reaches_controls_and_follows_the_language()
    {
        Loc.Instance.SetCulture("zh-CN");
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Light;
        var w = new MainWindow { DataContext = new ShellViewModel(_ => throw new InvalidOperationException()), Width = 900, Height = 500 };
        w.Show();
        try
        {
            CarbonFonts.Apply(app.Resources, japanese: false);
            MainWindowUiTestsHelpers.Pump();
            var label = w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "主机");
            Assert.Contains("Fonts#IBM Plex Sans, PingFang SC", label.FontFamily.ToString(), StringComparison.Ordinal);
            MainWindowUiTestsHelpers.Save(w, "31-nav-zh-fonts");

            CarbonFonts.Apply(app.Resources, japanese: true);
            MainWindowUiTestsHelpers.Pump();
            Assert.Contains("Fonts#IBM Plex Sans, Hiragino Sans", label.FontFamily.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            app.Resources.Remove("CarbonFontSans");
            app.Resources.Remove("CarbonFontMono");
            app.Resources.Remove("ContentControlThemeFontFamily");
            Loc.Instance.SetCulture("en-US");
            w.Close();
        }
    }
}
