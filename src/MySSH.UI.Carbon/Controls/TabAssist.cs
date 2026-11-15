using Avalonia;
using Avalonia.Controls;

namespace MySSH.UI.Carbon;

/// <summary>Tab strip decorations: <c>carbon:TabAssist.TrailingContent</c> is shown right after the tabs.</summary>
public sealed class TabAssist
{
    private TabAssist()
    {
    }

    /// <summary>Content placed after the last tab (e.g. a "new session" icon button).</summary>
    public static readonly AttachedProperty<object?> TrailingContentProperty =
        AvaloniaProperty.RegisterAttached<TabAssist, TabControl, object?>("TrailingContent");

    public static object? GetTrailingContent(TabControl c) => c.GetValue(TrailingContentProperty);

    public static void SetTrailingContent(TabControl c, object? value) => c.SetValue(TrailingContentProperty, value);
}
