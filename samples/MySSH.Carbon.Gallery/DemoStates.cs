using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace MySSH.Carbon.Gallery;

/// <summary>
/// Forces visual states for the static gallery: controls with Classes="demo-hover" / "demo-focus" / "demo-pressed"
/// get the matching pseudo-classes, so hover / focus looks can be reviewed (and screenshot) without a pointer.
/// </summary>
public static class DemoStates
{
    public static void Apply(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ApplyNow(root);

        // some controls (e.g. Button :pressed) reset their pseudo-classes when attached; apply again once loaded
        root.Loaded += (_, _) => ApplyNow(root);
    }

    private static void ApplyNow(Control root)
    {
        foreach (var c in root.GetSelfAndLogicalDescendants().OfType<Control>())
        {
            var pc = (IPseudoClasses)c.Classes;
            if (c.Classes.Contains("demo-hover"))
            {
                pc.Set(":pointerover", true);
            }

            if (c.Classes.Contains("demo-pressed"))
            {
                pc.Set(":pressed", true);
            }

            if (c.Classes.Contains("demo-focus"))
            {
                pc.Set(":focus", true);
                pc.Set(":focus-visible", true);
                pc.Set(":focus-within", true);
            }
        }
    }
}
