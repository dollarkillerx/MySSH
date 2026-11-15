using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace MySSH.UI.Carbon;

/// <summary>
/// Carbon UI Shell side-nav rail: 48px wide with icons, expands to 256px (<see cref="IsExpanded"/>) showing labels.
/// Items are <see cref="SideNavItem"/>s (icon + label); the selected item gets the 3px interactive indicator.
/// Always Gray 100 by default, like <see cref="ShellHeader"/>. <see cref="FooterContent"/> is docked at the bottom.
/// </summary>
public class SideNavRail : ListBox
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SideNavRail, bool>(nameof(IsExpanded), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> CollapsedWidthProperty =
        AvaloniaProperty.Register<SideNavRail, double>(nameof(CollapsedWidth), 48);

    public static readonly StyledProperty<double> ExpandedWidthProperty =
        AvaloniaProperty.Register<SideNavRail, double>(nameof(ExpandedWidth), 256);

    public static readonly StyledProperty<object?> FooterContentProperty =
        AvaloniaProperty.Register<SideNavRail, object?>(nameof(FooterContent));

    /// <summary>Theme of the rail. Default Dark (Carbon Gray 100), regardless of the app theme.</summary>
    public static readonly StyledProperty<ThemeVariant?> RequestedThemeVariantProperty =
        ThemeVariantScope.RequestedThemeVariantProperty.AddOwner<SideNavRail>();

    public SideNavRail()
    {
        SetCurrentValue(RequestedThemeVariantProperty, ThemeVariant.Dark);
        UpdateWidth();
    }

    /// <summary>Expanded (256px, labels visible) or rail (48px, icons + tooltips).</summary>
    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public double CollapsedWidth
    {
        get => GetValue(CollapsedWidthProperty);
        set => SetValue(CollapsedWidthProperty, value);
    }

    public double ExpandedWidth
    {
        get => GetValue(ExpandedWidthProperty);
        set => SetValue(ExpandedWidthProperty, value);
    }

    /// <summary>Content docked at the bottom of the rail (e.g. a settings item).</summary>
    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    /// <inheritdoc cref="RequestedThemeVariantProperty"/>
    public ThemeVariant? RequestedThemeVariant
    {
        get => GetValue(RequestedThemeVariantProperty);
        set => SetValue(RequestedThemeVariantProperty, value);
    }

    /// <inheritdoc />
    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new SideNavItem();

    /// <inheritdoc />
    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey) =>
        NeedsContainer<SideNavItem>(item, out recycleKey);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RequestedThemeVariantProperty)
        {
            if (change.GetNewValue<ThemeVariant?>() is { } v && v != ThemeVariant.Default)
            {
                SetValue(ThemeVariantScope.ActualThemeVariantProperty, v);
            }
            else
            {
                ClearValue(ThemeVariantScope.ActualThemeVariantProperty);
            }
        }
        else if (change.Property == IsExpandedProperty || change.Property == CollapsedWidthProperty ||
                 change.Property == ExpandedWidthProperty)
        {
            PseudoClasses.Set(":expanded", IsExpanded);
            UpdateWidth();
        }
    }

    private void UpdateWidth() => SetCurrentValue(WidthProperty, IsExpanded ? ExpandedWidth : CollapsedWidth);
}

/// <summary>An item of <see cref="SideNavRail"/>: <see cref="Icon"/> (Carbon icon kind) + label (Content).</summary>
public class SideNavItem : ListBoxItem
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<SideNavItem, string?>(nameof(Icon));

    /// <summary>16px Carbon icon kind.</summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
