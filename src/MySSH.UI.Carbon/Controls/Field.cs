using Avalonia;
using Avalonia.Controls;

namespace MySSH.UI.Carbon;

/// <summary>
/// Carbon form-field decorations for TextBox / ComboBox:
/// <c>carbon:Field.Label</c> (label-01 above), <c>carbon:Field.HelperText</c> (helper-text-01 below),
/// <c>carbon:Field.IsInvalid</c> + <c>carbon:Field.InvalidText</c> (red outline, warning icon, error text).
/// Setting <see cref="IsInvalidProperty"/> also sets the <c>:invalid</c> pseudo-class on the control.
/// Data-validation errors (<c>:error</c>) are shown the same way.
/// </summary>
public sealed class Field
{
    private Field()
    {
    }

    /// <summary>Label shown above the field.</summary>
    public static readonly AttachedProperty<string?> LabelProperty =
        AvaloniaProperty.RegisterAttached<Field, Control, string?>("Label");

    /// <summary>Helper text shown below the field (replaced by <see cref="InvalidTextProperty"/> when invalid).</summary>
    public static readonly AttachedProperty<string?> HelperTextProperty =
        AvaloniaProperty.RegisterAttached<Field, Control, string?>("HelperText");

    /// <summary>Error message shown below the field when <see cref="IsInvalidProperty"/> is true.</summary>
    public static readonly AttachedProperty<string?> InvalidTextProperty =
        AvaloniaProperty.RegisterAttached<Field, Control, string?>("InvalidText");

    /// <summary>Marks the field invalid (Carbon "invalid" state). Sets the <c>:invalid</c> pseudo-class.</summary>
    public static readonly AttachedProperty<bool> IsInvalidProperty =
        AvaloniaProperty.RegisterAttached<Field, Control, bool>("IsInvalid");

    static Field()
    {
        IsInvalidProperty.Changed.AddClassHandler<Control>((c, e) =>
            ((IPseudoClasses)c.Classes).Set(":invalid", e.NewValue is true));
    }

    public static string? GetLabel(Control c) => c.GetValue(LabelProperty);

    public static void SetLabel(Control c, string? value) => c.SetValue(LabelProperty, value);

    public static string? GetHelperText(Control c) => c.GetValue(HelperTextProperty);

    public static void SetHelperText(Control c, string? value) => c.SetValue(HelperTextProperty, value);

    public static string? GetInvalidText(Control c) => c.GetValue(InvalidTextProperty);

    public static void SetInvalidText(Control c, string? value) => c.SetValue(InvalidTextProperty, value);

    public static bool GetIsInvalid(Control c) => c.GetValue(IsInvalidProperty);

    public static void SetIsInvalid(Control c, bool value) => c.SetValue(IsInvalidProperty, value);
}
