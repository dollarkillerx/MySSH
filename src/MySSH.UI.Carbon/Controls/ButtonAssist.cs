using Avalonia;
using Avalonia.Controls;

namespace MySSH.UI.Carbon;

/// <summary>
/// Button decorations: <c>carbon:ButtonAssist.Icon="add"</c> shows a 16px Carbon icon in the right-aligned icon
/// slot (or centered for <c>Classes="icon-only"</c>). Sets the <c>:has-icon</c> pseudo-class.
/// </summary>
public sealed class ButtonAssist
{
    private ButtonAssist()
    {
    }

    /// <summary>Icon kind (see <see cref="CarbonIconNames"/>).</summary>
    public static readonly AttachedProperty<string?> IconProperty =
        AvaloniaProperty.RegisterAttached<ButtonAssist, Control, string?>("Icon");

    static ButtonAssist()
    {
        IconProperty.Changed.AddClassHandler<Control>((c, e) =>
            ((IPseudoClasses)c.Classes).Set(":has-icon", !string.IsNullOrEmpty(e.NewValue as string)));
    }

    public static string? GetIcon(Control c) => c.GetValue(IconProperty);

    public static void SetIcon(Control c, string? value) => c.SetValue(IconProperty, value);
}
