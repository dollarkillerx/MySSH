using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MySSH.UI.Carbon.Tests;

/// <summary>Opens the popup-based controls (dropdown, menu flyout, tooltip) and renders their popups.</summary>
public class PopupTests
{
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Dropdown_menu_and_tooltip_popups_render(string variant)
    {
        LogCollector.Drain();
        var theme = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var combo = new ComboBox { Width = 280, ItemsSource = new[] { "Password", "Private key", "SSH agent" }, SelectedIndex = 1 };
        Field.SetLabel(combo, "Authentication");
        var menuButton = new Button { Content = "Options", Classes = { "ghost" } };
        var flyout = new MenuFlyout
        {
            Items =
            {
                new MenuItem { Header = "Connect" },
                new MenuItem { Header = "Open SFTP", InputGesture = Avalonia.Input.KeyGesture.Parse("Ctrl+O") },
                new Separator(),
                new MenuItem { Header = "Delete", Classes = { "danger" } },
            },
        };
        menuButton.Flyout = flyout;
        var tipTarget = new Button { Content = "Hover me", Classes = { "secondary" } };
        ToolTip.SetTip(tipTarget, "Host keys are verified on every hop.");

        var panel = new StackPanel { Margin = new Avalonia.Thickness(32), Spacing = 24, Children = { combo, menuButton, tipTarget } };
        var window = Render.Show(panel, theme, 600, 400);

        combo.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        SavePopup(combo, $"popup-dropdown-{variant}");
        combo.IsDropDownOpen = false;

        flyout.ShowAt(menuButton);
        Dispatcher.UIThread.RunJobs();
        var presenter = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().FirstOrDefault()
            ?? TopLevelsPresenters().FirstOrDefault();
        Assert.NotNull(presenter);
        SaveTopLevelOf(presenter!, $"popup-menu-{variant}");
        flyout.Hide();

        ToolTip.SetIsOpen(tipTarget, true);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ToolTip.GetIsOpen(tipTarget));
        ToolTip.SetIsOpen(tipTarget, false);

        var logs = LogCollector.Drain();
        window.Close();
        Assert.True(logs.Count == 0, string.Join("\n", logs));
    }

    private static IEnumerable<MenuFlyoutPresenter> TopLevelsPresenters() => [];

    private static void SavePopup(ComboBox combo, string name)
    {
        var item = combo.GetVisualDescendants().OfType<Popup>().First().Child;
        Assert.NotNull(item);
        SaveTopLevelOf(item!, name);
    }

    private static void SaveTopLevelOf(Control c, string name)
    {
        for (var i = 0; i < 30; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        var top = TopLevel.GetTopLevel(c);
        Assert.NotNull(top);
        var frame = top!.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(Render.WorkDir(), name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
