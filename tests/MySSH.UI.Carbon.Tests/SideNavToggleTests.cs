using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MySSH.UI.Carbon.Tests;

/// <summary>
/// The header menu button must expand AND collapse the rail. Regression: SideNavRail set Width with SetCurrentValue,
/// which the Width transition captured in its animation layer; when the animation ended Width fell back to NaN (auto),
/// so the rail stayed wide and stopped following IsExpanded. The base (non-animated) Width must track IsExpanded.
/// </summary>
public sealed class SideNavToggleTests
{
    [AvaloniaFact]
    public void Menu_button_expands_and_collapses_the_rail()
    {
        var header = new ShellHeader { ProductPrefix = "MySSH", IsMenuButtonVisible = true };
        var rail = new SideNavRail { ItemsSource = new[] { "Hosts", "Sessions" } };
        header.Bind(ShellHeader.IsMenuOpenProperty, new Binding(nameof(SideNavRail.IsExpanded)) { Source = rail, Mode = BindingMode.TwoWay });
        var dock = new DockPanel { Children = { header, rail, new Border() } };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(rail, Dock.Left);
        var w = new Window { Content = dock, Width = 800, Height = 500 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var button = header.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_MenuButton");
        Assert.Equal(48d, rail.GetBaseValue(Layoutable.WidthProperty).Value);

        for (var i = 1; i <= 4; i++)
        {
            var p = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), w)!.Value;
            w.MouseDown(p, MouseButton.Left);
            w.MouseUp(p, MouseButton.Left);
            for (var t = 0; t < 10; t++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(); // let the transition start and run a few frames
                Dispatcher.UIThread.RunJobs();
            }
            var open = i % 2 == 1;
            Assert.Equal(open, header.IsMenuOpen);
            Assert.Equal(open, rail.IsExpanded);
            var baseWidth = rail.GetBaseValue(Layoutable.WidthProperty);
            Assert.True(baseWidth.HasValue, $"click {i}: Width only lives in the animation layer");
            Assert.Equal(open ? 256d : 48d, baseWidth.Value);
        }
        w.Close();
    }
}
