using Avalonia.Controls;
using MySSH.Carbon.Gallery.Pages;

namespace MySSH.Carbon.Gallery;

/// <summary>A gallery page: id (used for screenshot file names), title, rail icon and factory.</summary>
public sealed record GalleryPage(string Id, string Title, string Icon, Func<Control> Create);

/// <summary>All gallery pages, in navigation order.</summary>
public static class GalleryPages
{
    public static IReadOnlyList<GalleryPage> All { get; } =
    [
        new("buttons", "Buttons", "application", () => new ButtonsPage()),
        new("inputs", "Inputs", "edit", () => new InputsPage()),
        new("selection", "Selection", "checkmark-outline", () => new SelectionPage()),
        new("tabs", "Tabs", "switcher", () => new TabsPage()),
        new("datatable", "Data table", "data-table", () => new DataTablePage()),
        new("lists", "Lists & menus", "list", () => new ListsPage()),
        new("feedback", "Notifications & tags", "notification", () => new FeedbackPage()),
        new("shell", "UI shell & modal", "server", () => new ShellPage()),
        new("icons", "Icons", "star", () => new IconsPage()),
        new("typography", "Typography", "text-font", () => new TypographyPage()),
        new("colors", "Color tokens", "color-palette", () => new ColorsPage()),
    ];
}
