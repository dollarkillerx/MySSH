using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using MySSH.Terminal.Rendering;

namespace MySSH.Terminal.Tests;

public class SearchTests
{
    private const string E = Harness.E;

    private static Harness WithLog()
    {
        var h = new Harness();
        for (var i = 0; i < 300; i++)
        {
            h.View.Feed(i % 50 == 7 ? $"{i:D3} ERROR upstream timed out (error code {i})\r\n" : $"{i:D3} info request ok\r\n");
        }

        h.Pump();
        return h;
    }

    [AvaloniaFact]
    public void Find_previous_starts_at_the_bottom_and_reveals_hits_in_scrollback()
    {
        var h = WithLog();
        Assert.True(h.View.FindPrevious("error"));
        Assert.Equal(12, h.View.SearchResultCount); // "ERROR" and "error" on 6 lines
        Assert.Equal(11, h.View.SearchResultIndex);
        for (var i = 0; i < 4; i++)
        {
            h.View.FindPrevious("error");
        }

        // hit index 7 is on line 157 ("ERROR" and "error" per line → 2 hits per line): far up in the scrollback
        Assert.Equal(7, h.View.SearchResultIndex);
        Assert.True(h.View.IsScrolledBack);
        Assert.Contains("157 ERROR", string.Join('\n', Enumerable.Range(0, h.View.Rows)
            .Select(r => h.View.Engine.Buffer.GetLine(h.View.Engine.Buffer.YDisp + r)!.TranslateToString(true, 0, h.View.Columns))), StringComparison.Ordinal);
        h.Save("60-search-hits");
    }

    [AvaloniaFact]
    public void Match_case_find_next_wraps_and_clear_removes_highlights()
    {
        var h = WithLog();
        Assert.True(h.View.FindNext("ERROR", matchCase: true));
        Assert.Equal(6, h.View.SearchResultCount);
        var first = h.View.SearchResultIndex;
        for (var i = 0; i < 6; i++)
        {
            h.View.FindNext("ERROR", matchCase: true);
        }

        Assert.Equal(first, h.View.SearchResultIndex);
        Assert.False(h.View.FindNext("no such thing"));
        Assert.Equal(0, h.View.SearchResultCount);
        h.View.FindNext("ERROR", true);
        h.View.ClearSearch();
        Assert.Equal(0, h.View.SearchResultCount);
        Assert.True(h.View.RowVisuals.All(r => r.Deco.SearchStamp == 0));
    }

    [AvaloniaFact]
    public void Search_hit_light_theme()
    {
        var h = new Harness(theme: ThemeVariant.Light);
        h.Feed(RenderingTests.Sample);
        Assert.True(h.View.FindNext("deploy"));
        h.View.FindNext("deploy");
        h.Save("61-search-light");
    }
}

public class LinkTests
{
    private const string E = Harness.E;

    [AvaloniaFact]
    public void Urls_are_detected_with_trailing_punctuation_trimmed()
    {
        var h = new Harness();
        h.Feed("see https://example.com/docs/a_b?x=1&y=(2). and (www.example.org/path) done");
        var link = h.View.FindLink(10, 0);
        Assert.NotNull(link);
        Assert.Equal(new Uri("https://example.com/docs/a_b?x=1&y=(2)"), link.Uri);
        var www = h.View.FindLink(52, 0);
        Assert.Equal(new Uri("http://www.example.org/path"), www!.Uri);
        Assert.Null(h.View.FindLink(1, 0));
    }

    [AvaloniaFact]
    public void Wrapped_url_spans_rows_and_osc8_links_win()
    {
        var h = new Harness(400, 200);
        var cols = h.View.Columns;
        var url = "https://example.com/" + new string('p', cols);
        h.Feed("x " + url + "\r\n");
        var hit = h.View.FindLink(3, 0);
        Assert.Equal(new Uri(url), hit!.Uri);
        Assert.Equal(2, hit.Segments.Count);
        Assert.Equal(hit.Uri, h.View.FindLink(4, 1)!.Uri);

        h.Feed($"{E}]8;;https://docs.example.test/page{E}\\click here{E}]8;;{E}\\ plain");
        var osc8 = h.View.FindLink(3, h.Cursor.Y);
        Assert.True(osc8!.IsHyperlink);
        Assert.Equal(new Uri("https://docs.example.test/page"), osc8.Uri);
        Assert.Null(h.View.FindLink(13, h.Cursor.Y));
    }

    [AvaloniaFact]
    public void Ctrl_click_raises_link_activated_and_hover_underlines()
    {
        var h = new Harness();
        var wasMac = TerminalView.IsMac;
        try
        {
            TerminalView.IsMac = false;
            h.Feed("open https://example.com/x now");
            Uri? activated = null;
            h.View.LinkActivated += (_, u) => activated = u;
            var c = h.View.CellSize;
            var p = new Point(10.5 * c.Width, 0.5 * c.Height);
            h.Window.MouseMove(p, RawInputModifiers.Control);
            h.Pump();
            Assert.Equal(5, h.View.RowVisuals.Single(r => r.Slot == 0).Deco.HoverStart);
            h.Save("70-link-hover");
            h.Window.MouseDown(p, MouseButton.Left, RawInputModifiers.Control);
            h.Window.MouseUp(p, MouseButton.Left, RawInputModifiers.Control);
            Assert.Equal(new Uri("https://example.com/x"), activated);
            Assert.False(h.View.HasSelection);

            activated = null;
            h.Window.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
            h.Window.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
            Assert.Null(activated); // a plain click never opens links
        }
        finally
        {
            TerminalView.IsMac = wasMac;
        }
    }
}

public class ThemeAndAccessibilityTests
{
    private const string E = Harness.E;

    [AvaloniaFact]
    public void Default_theme_follows_carbon_tokens_and_variant()
    {
        var h = new Harness(theme: ThemeVariant.Dark);
        Assert.Equal(Color.Parse("#161616"), h.View.ResolvedTheme!.Background);
        Assert.Equal(Color.Parse("#f4f4f4"), h.View.ResolvedTheme.Foreground);
        h.Window.RequestedThemeVariant = ThemeVariant.Light;
        h.Pump();
        Assert.Equal(Color.Parse("#ffffff"), h.View.ResolvedTheme!.Background);
        Assert.Equal(Color.Parse("#161616"), h.View.ResolvedTheme.Foreground);
        // the engine answers OSC 11 with the themed background (vim/neovim pick dark/light from it)
        var b = new FakeBackend();
        h.View.Attach(b);
        b.Emit($"{E}]11;?\u0007");
        h.Pump();
        Assert.Contains("rgb:ffff/ffff/ffff", b.WrittenText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Custom_theme_overrides_everything()
    {
        var theme = TerminalTheme.CarbonDark.Clone();
        theme.Background = Color.Parse("#002b36");
        var ansi = theme.Ansi.ToArray();
        ansi[1] = Color.Parse("#dc322f");
        theme.Ansi = ansi;
        var h = new Harness(configure: v => v.ColorTheme = theme);
        Assert.Equal(theme.Background, h.View.ResolvedTheme!.Background);
        h.Feed($"{E}[41m   {E}[0m");
        var frame = h.Capture();
        Assert.Equal((0x00, 0x2b, 0x36), Harness.Pixel(frame, 200, 200));
        Assert.Equal((0xdc, 0x32, 0x2f), Harness.Pixel(frame, 4, 4));
    }

    [AvaloniaFact]
    public void Light_theme_keeps_low_contrast_colours_readable()
    {
        var palette = new RenderPalette(TerminalTheme.CarbonLight, new XTerm.Common.ColorPalette(TerminalTheme.CarbonLight.ToEngineTheme()));
        foreach (var index in new[] { 3, 7, 11, 15, 226, 229, 255 })
        {
            var fg = palette.Contrast(palette.Indexed(index), palette.Background);
            Assert.True(TerminalTheme.ContrastRatio(Color.FromUInt32(fg), Color.FromUInt32(palette.Background)) >= 3, $"colour {index}");
        }

        // explicit backgrounds keep the program's intent: black on white stays black
        Assert.Equal(palette.Indexed(0), palette.Contrast(palette.Indexed(0), 0xFFFFFFFF));
    }

    [AvaloniaFact]
    public void Automation_peer_exposes_the_visible_text()
    {
        var h = new Harness();
        h.Feed("$ uname\r\nLinux");
        var peer = ControlAutomationPeer.CreatePeerForElement(h.View);
        var value = peer.GetProvider<IValueProvider>();
        Assert.NotNull(value);
        Assert.True(value.IsReadOnly);
        Assert.Equal("$ uname\nLinux", value.Value);
        Assert.Equal(AutomationControlType.Document, peer.GetAutomationControlType());
        Assert.Equal("Terminal", peer.GetName());
    }

    [AvaloniaFact]
    public void Font_size_change_rebuilds_the_grid()
    {
        var h = new Harness();
        var cols = h.View.Columns;
        var b = new FakeBackend();
        h.View.Attach(b);
        h.View.FontSize = 20;
        h.Pump();
        Assert.True(h.View.Columns < cols);
        Assert.Equal((h.View.Columns, h.View.Rows), b.Resizes[^1]);
    }
}
