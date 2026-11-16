using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MySSH.Desktop.Localization;
using MySSH.Desktop.ViewModels;
using MySSH.UI.Carbon;

namespace MySSH.Desktop.Tests;

[Collection("Loc")]
/// <summary>Rail text must stay light (dark rail) when hovered, also after the saved theme is applied at runtime.</summary>
public sealed class NavColorTests
{
    [AvaloniaFact]
    public void Hovered_and_selected_items_stay_readable_on_the_dark_rail_after_a_theme_switch()
    {
        Loc.Instance.SetCulture("zh-CN");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark; // OS theme at startup
        var vm = new ShellViewModel(_ => throw new InvalidOperationException());
        var w = new MainWindow { DataContext = vm, Width = 900, Height = 600 };
        w.Show();
        var nav = w.FindControl<SideNavRail>("Nav")!;
        nav.IsExpanded = true;
        nav.SelectedIndex = 5;
        for (var i = 0; i < 10; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        Application.Current.RequestedThemeVariant = ThemeVariant.Light; // saved preference applied after unlock
        for (var i = 0; i < 30; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        var items = nav.GetVisualDescendants().OfType<SideNavItem>().ToList();
        var p = items[2].TranslatePoint(new Point(100, items[2].Bounds.Height / 2), w)!.Value;
        w.MouseMove(p);
        for (var i = 0; i < 30; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        foreach (var it in items)
        {
            var tb = it.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == (string)it.Content!);
            var color = Assert.IsAssignableFrom<ISolidColorBrush>(tb.Foreground).Color;
            // Carbon g100: text-secondary #c6c6c6, text-primary #f4f4f4; never the light theme's #161616 / #525252
            Assert.True(color.R > 0xb0 && color.G > 0xb0 && color.B > 0xb0, $"{tb.Text}: {color} ({string.Join(',', it.Classes)})");
        }
        Assert.Contains(":pointerover", items[2].Classes);
        MainWindowUiTestsHelpers.Save(w, "30-nav-hover-light-zh");
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        Loc.Instance.SetCulture("en-US");
        w.Close();
    }

    /// <summary>Regression (crash): the collapsed rail's tooltip reused the label control, which already had a parent.</summary>
    [AvaloniaFact]
    public void Collapsed_rail_tooltip_shows_the_label_without_throwing()
    {
        Loc.Instance.SetCulture("zh-CN");
        var errors = new List<Exception>();
        void OnError(object? s, Avalonia.Threading.DispatcherUnhandledExceptionEventArgs e) { errors.Add(e.Exception); e.Handled = true; }
        Dispatcher.UIThread.UnhandledException += OnError;
        var w = new MainWindow { DataContext = new ShellViewModel(_ => throw new InvalidOperationException()), Width = 900, Height = 600 };
        try
        {
            w.Show();
            var nav = w.FindControl<SideNavRail>("Nav")!;
            Assert.False(nav.IsExpanded);
            foreach (var item in nav.GetVisualDescendants().OfType<SideNavItem>().ToList())
            {
                ToolTip.SetIsOpen(item, true);
                for (var i = 0; i < 5; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
                Assert.True(ToolTip.GetIsOpen(item));
                Assert.Equal(item.Content, ToolTip.GetTip(item));
                ToolTip.SetIsOpen(item, false);
            }
            Assert.Equal("已知主机", ToolTip.GetTip(nav.GetVisualDescendants().OfType<SideNavItem>().ElementAt(3)));
            Assert.Empty(errors);
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= OnError;
            Loc.Instance.SetCulture("en-US");
            w.Close();
        }
    }
}
