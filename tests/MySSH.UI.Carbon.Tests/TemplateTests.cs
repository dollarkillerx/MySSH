using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MySSH.UI.Carbon.Tests;

public class TemplateTests
{
    /// <summary>Control factory + a template part that only the Carbon template has.</summary>
    public static TheoryData<string> Cases() => new(Factories.Keys);

    private static readonly Dictionary<string, (Func<Control> Create, string Part)> Factories = new()
    {
        ["Button"] = (() => new Button { Content = "Button" }, "PART_FocusRing"),
        ["Button.icon"] = (() => WithIcon(new Button { Content = "Add", Classes = { "secondary" } }), "PART_Icon"),
        ["Button.icon-only"] = (() => WithIcon(new Button { Classes = { "ghost", "icon-only" } }), "PART_Icon"),
        ["ToggleButton"] = (() => new ToggleButton { Content = "Toggle", IsChecked = true }, "PART_FocusRing"),
        ["RepeatButton"] = (() => new RepeatButton { Content = "+" }, "PART_ContentPresenter"),
        ["TextBox"] = (() => Field(new TextBox { Text = "abc" }), "PART_FocusRing"),
        ["TextBox.password"] = (() => new TextBox { Classes = { "password" }, PasswordChar = '●', Text = "x" }, "PART_InvalidIcon"),
        ["TextBox.invalid"] = (() => Invalid(new TextBox()), "PART_InvalidText"),
        ["TextBox.area"] = (() => new TextBox { AcceptsReturn = true, Height = 100 }, "PART_FocusRing"),
        ["ComboBox"] = (() => Field(new ComboBox { ItemsSource = new[] { "a", "b" }, SelectedIndex = 0 }), "PART_Chevron"),
        ["ComboBoxItem"] = (() => new ComboBoxItem { Content = "x", IsSelected = true }, "PART_Check"),
        ["CheckBox"] = (() => new CheckBox { Content = "Check", IsChecked = true }, "PART_CheckMark"),
        ["RadioButton"] = (() => new RadioButton { Content = "Radio", IsChecked = true }, "PART_Dot"),
        ["ToggleSwitch"] = (() => new ToggleSwitch { Content = "Toggle", IsChecked = true }, "PART_Track"),
        ["TabControl"] = (() => new TabControl { Items = { new TabItem { Header = "A" }, new TabItem { Header = "B" } } }, "PART_StripScroller"),
        ["TabControl.contained"] = (() => new TabControl { Classes = { "contained" }, Items = { new TabItem { Header = "A" } } }, "PART_StripScroller"),
        ["TabItem"] = (() => new TabItem { Header = "Tab" }, "PART_Indicator"),
        ["ListBox"] = (() => new ListBox { ItemsSource = new[] { "a", "b" }, SelectedIndex = 0 }, "PART_ScrollViewer"),
        ["ListBoxItem"] = (() => new ListBoxItem { Content = "x" }, "PART_Indicator"),
        ["MenuFlyoutPresenter"] = (() => new MenuFlyoutPresenter { Items = { new MenuItem { Header = "Open" }, new Separator(), new MenuItem { Header = "Delete", Classes = { "danger" } } } }, "PART_ItemsPresenter"),
        ["MenuItem"] = (() => new MenuItem { Header = "Item", InputGesture = Avalonia.Input.KeyGesture.Parse("Ctrl+C") }, "PART_SubmenuChevron"),
        ["Menu"] = (() => new Menu { Items = { new MenuItem { Header = "File" } } }, "PART_ItemsPresenter"),
        ["ContextMenu"] = (() => new ContextMenu { Items = { new MenuItem { Header = "Copy" } } }, "PART_ItemsPresenter"),
        ["ToolTip"] = (() => new ToolTip { Content = "Tip" }, "PART_ContentPresenter"),
        ["ProgressBar"] = (() => Field(new ProgressBar { Value = 40 }), "PART_Track"),
        ["ProgressBar.indeterminate"] = (() => new ProgressBar { IsIndeterminate = true }, "PART_Track"),
        ["ScrollViewer"] = (() => new ScrollViewer { Height = 50, Content = new Border { Height = 500 } }, "PART_VerticalScrollBar"),
        ["ScrollBar"] = (() => new ScrollBar { Orientation = Avalonia.Layout.Orientation.Vertical, Height = 100, ViewportSize = 10, Maximum = 100 }, "PART_Track"),
        ["FlyoutPresenter"] = (() => new FlyoutPresenter { Content = "Flyout" }, "PART_ContentPresenter"),
        ["DataGrid"] = (() => Grid(), "PART_ColumnHeadersPresenter"),
        ["Tag"] = (() => new Tag { Type = TagType.Blue, Content = "Tag", IsDismissible = true, Icon = "server" }, "PART_DismissButton"),
        ["InlineNotification"] = (() => new InlineNotification { Kind = NotificationKind.Warning, Title = "T", Subtitle = "S" }, "PART_CloseButton"),
        ["InlineNotification.low"] = (() => new InlineNotification { Kind = NotificationKind.Error, Title = "T", IsLowContrast = true }, "PART_Outline"),
        ["Modal"] = (() => new Modal { IsOpen = true, Heading = "Heading", Label = "Label", Content = "Body", IsDanger = true }, "PART_PrimaryButton"),
        ["ShellHeader"] = (() => new ShellHeader { ProductPrefix = "MySSH", ProductName = "Test", IsMenuButtonVisible = true }, "PART_MenuButton"),
        ["SideNavRail"] = (() => new SideNavRail { ItemsSource = new[] { "Hosts", "Keys" }, SelectedIndex = 0, Height = 200 }, "PART_ScrollViewer"),
        ["SideNavItem"] = (() => new SideNavItem { Icon = "home", Content = "Home" }, "PART_Indicator"),
    };

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void Carbon_template_applies_without_binding_errors(string name)
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            LogCollector.Drain();
            var (create, part) = Factories[name];
            var control = create();
            var window = Render.Show(new StackPanel { Children = { control } }, theme, 600, 400);
            Dispatcher.UIThread.RunJobs();

            var templated = control as TemplatedControl;
            Assert.NotNull(templated);
            Assert.NotEmpty(control.GetVisualChildren());
            var names = control.GetVisualDescendants().OfType<Control>().Select(c => c.Name).Where(n => n is not null).ToHashSet();
            Assert.True(names.Contains(part), $"{name}: template part {part} not found (got: {string.Join(", ", names)})");

            var logs = LogCollector.Drain();
            Assert.True(logs.Count == 0, $"{name} ({theme}):\n{string.Join("\n", logs)}");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Header_and_rail_stay_gray_100_in_the_light_theme()
    {
        var header = new ShellHeader { ProductName = "x" };
        var rail = new SideNavRail { ItemsSource = new[] { "a" }, Height = 100 };
        var window = Render.Show(new StackPanel { Children = { header, rail } }, ThemeVariant.Light, 600, 300);
        Assert.Equal(ThemeVariant.Dark, header.ActualThemeVariant);
        Assert.Equal(ThemeVariant.Dark, rail.ActualThemeVariant);
        var bg = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(header.Background);
        Assert.Equal(Avalonia.Media.Color.Parse("#161616"), bg.Color);
        var item = rail.GetLogicalDescendants().OfType<SideNavItem>().First();
        Assert.Equal(ThemeVariant.Dark, item.ActualThemeVariant);
        window.Close();
    }

    [AvaloniaFact]
    public void Icons_resolve_for_every_kind_and_size()
    {
        foreach (var kind in CarbonIconNames.All)
        {
            foreach (var size in new[] { 16d, 20, 24, 32 })
            {
                var icon = new CarbonIcon { Kind = kind, Size = size };
                var window = Render.Show(new StackPanel { Children = { icon } }, ThemeVariant.Light, 100, 100);
                Assert.True(icon.HasIcon, $"{kind}@{size}");
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void Modal_close_button_and_primary_raise_results()
    {
        var modal = new Modal { Heading = "h" };
        var root = new Panel { Width = 800, Height = 600 };
        var window = Render.Show(root, ThemeVariant.Light, 800, 600);
        var task = modal.ShowAsync(root);
        Dispatcher.UIThread.RunJobs();
        Assert.True(modal.IsVisible);
        var primary = modal.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_PrimaryButton");
        primary.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(task.IsCompleted);
        Assert.Equal(ModalResult.Primary, task.Result);
        Assert.False(modal.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Log_collector_sees_binding_errors()
    {
        LogCollector.Drain();
        var tb = new TextBlock { DataContext = new Row("a", 1) };
        tb.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("DoesNotExist"));
        var window = Render.Show(new StackPanel { Children = { tb } }, ThemeVariant.Light, 200, 100);
        var logs = LogCollector.Drain();
        window.Close();
        Assert.Contains(logs, l => l.Contains("DoesNotExist", StringComparison.Ordinal));
    }

    private static Button WithIcon(Button b)
    {
        ButtonAssist.SetIcon(b, "add");
        return b;
    }

    private static T Field<T>(T c)
        where T : Control
    {
        MySSH.UI.Carbon.Field.SetLabel(c, "Label");
        MySSH.UI.Carbon.Field.SetHelperText(c, "Helper");
        return c;
    }

    private static TextBox Invalid(TextBox t)
    {
        MySSH.UI.Carbon.Field.SetIsInvalid(t, true);
        MySSH.UI.Carbon.Field.SetInvalidText(t, "Invalid");
        return t;
    }

    private static DataGrid Grid()
    {
        var grid = new DataGrid
        {
            Height = 200,
            ItemsSource = new[] { new Row("a", 1), new Row("b", 2) },
            Columns =
            {
                new DataGridTextColumn { Header = "Name", Binding = new Avalonia.Data.Binding(nameof(Row.Name)) },
                new DataGridTextColumn { Header = "Value", Binding = new Avalonia.Data.Binding(nameof(Row.Value)) },
            },
        };
        return grid;
    }

    public sealed record Row(string Name, int Value);
}
