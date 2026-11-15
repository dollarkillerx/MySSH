using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace MySSH.UI.Carbon;

/// <summary>
/// Carbon UI Shell header: 48px bar, always Gray 100 (<see cref="RequestedThemeVariant"/> defaults to Dark),
/// optional menu button (<see cref="IsMenuOpen"/> drives a <see cref="SideNavRail"/>), product name
/// (<see cref="ProductPrefix"/> + <see cref="ProductName"/>), free content in the middle
/// (<see cref="ContentControl.Content"/>, e.g. a search field) and <see cref="GlobalActions"/> on the right
/// (use <c>Button Classes="header-action"</c>).
/// Works as a custom title bar with <c>ExtendClientAreaToDecorationsHint="True"</c>: the background is a
/// title-bar drag area and <see cref="LeadingInset"/> / <see cref="TrailingInset"/> keep clear of the window
/// buttons (NaN = automatic: macOS traffic lights on the left, caption buttons on the right elsewhere).
/// </summary>
[TemplatePart("PART_MenuButton", typeof(Button))]
public class ShellHeader : ContentControl
{
    public static readonly StyledProperty<string?> ProductPrefixProperty =
        AvaloniaProperty.Register<ShellHeader, string?>(nameof(ProductPrefix));

    public static readonly StyledProperty<string?> ProductNameProperty =
        AvaloniaProperty.Register<ShellHeader, string?>(nameof(ProductName));

    public static readonly StyledProperty<object?> GlobalActionsProperty =
        AvaloniaProperty.Register<ShellHeader, object?>(nameof(GlobalActions));

    public static readonly StyledProperty<bool> IsMenuButtonVisibleProperty =
        AvaloniaProperty.Register<ShellHeader, bool>(nameof(IsMenuButtonVisible));

    public static readonly StyledProperty<bool> IsMenuOpenProperty =
        AvaloniaProperty.Register<ShellHeader, bool>(nameof(IsMenuOpen), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> LeadingInsetProperty =
        AvaloniaProperty.Register<ShellHeader, double>(nameof(LeadingInset), double.NaN);

    public static readonly StyledProperty<double> TrailingInsetProperty =
        AvaloniaProperty.Register<ShellHeader, double>(nameof(TrailingInset), double.NaN);

    /// <summary>Theme of the header itself. Default Dark (Carbon Gray 100), regardless of the app theme.</summary>
    public static readonly StyledProperty<ThemeVariant?> RequestedThemeVariantProperty =
        ThemeVariantScope.RequestedThemeVariantProperty.AddOwner<ShellHeader>();

    /// <summary>Effective padding that keeps the content clear of the window buttons.</summary>
    public static readonly DirectProperty<ShellHeader, Thickness> ChromePaddingProperty =
        AvaloniaProperty.RegisterDirect<ShellHeader, Thickness>(nameof(ChromePadding), o => o.ChromePadding);

    private Button? _menuButton;
    private Window? _window;
    private Thickness _chromePadding;

    public ShellHeader()
    {
        SetCurrentValue(RequestedThemeVariantProperty, ThemeVariant.Dark);
    }

    /// <summary>Bold part of the product name (Carbon "[Prefix] Name").</summary>
    public string? ProductPrefix
    {
        get => GetValue(ProductPrefixProperty);
        set => SetValue(ProductPrefixProperty, value);
    }

    /// <summary>Product name.</summary>
    public string? ProductName
    {
        get => GetValue(ProductNameProperty);
        set => SetValue(ProductNameProperty, value);
    }

    /// <summary>Right-aligned global actions (48x48 header-action buttons).</summary>
    public object? GlobalActions
    {
        get => GetValue(GlobalActionsProperty);
        set => SetValue(GlobalActionsProperty, value);
    }

    /// <summary>Shows the hamburger / close button on the left.</summary>
    public bool IsMenuButtonVisible
    {
        get => GetValue(IsMenuButtonVisibleProperty);
        set => SetValue(IsMenuButtonVisibleProperty, value);
    }

    /// <summary>Toggled by the menu button (bind to <see cref="SideNavRail.IsExpanded"/>).</summary>
    public bool IsMenuOpen
    {
        get => GetValue(IsMenuOpenProperty);
        set => SetValue(IsMenuOpenProperty, value);
    }

    /// <summary>Left inset in DIPs; NaN = automatic.</summary>
    public double LeadingInset
    {
        get => GetValue(LeadingInsetProperty);
        set => SetValue(LeadingInsetProperty, value);
    }

    /// <summary>Right inset in DIPs; NaN = automatic.</summary>
    public double TrailingInset
    {
        get => GetValue(TrailingInsetProperty);
        set => SetValue(TrailingInsetProperty, value);
    }

    /// <inheritdoc cref="RequestedThemeVariantProperty"/>
    public ThemeVariant? RequestedThemeVariant
    {
        get => GetValue(RequestedThemeVariantProperty);
        set => SetValue(RequestedThemeVariantProperty, value);
    }

    /// <inheritdoc cref="ChromePaddingProperty"/>
    public Thickness ChromePadding
    {
        get => _chromePadding;
        private set => SetAndRaise(ChromePaddingProperty, ref _chromePadding, value);
    }

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
        else if (change.Property == LeadingInsetProperty || change.Property == TrailingInsetProperty)
        {
            UpdateChromePadding();
        }
        else if (change.Property == IsMenuOpenProperty)
        {
            PseudoClasses.Set(":menu-open", change.GetNewValue<bool>());
        }
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_menuButton is not null)
        {
            _menuButton.Click -= OnMenuClick;
        }

        _menuButton = e.NameScope.Find<Button>("PART_MenuButton");
        if (_menuButton is not null)
        {
            _menuButton.Click += OnMenuClick;
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
        }

        UpdateChromePadding();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.IsExtendedIntoWindowDecorationsProperty || e.Property == Window.WindowStateProperty)
        {
            UpdateChromePadding();
        }
    }

    private void UpdateChromePadding()
    {
        var extended = _window is { IsExtendedIntoWindowDecorations: true, WindowState: not WindowState.FullScreen };
        var left = LeadingInset;
        var right = TrailingInset;
        if (double.IsNaN(left))
        {
            left = extended && OperatingSystem.IsMacOS() ? 72 : 0;
        }

        if (double.IsNaN(right))
        {
            right = extended && !OperatingSystem.IsMacOS() ? 138 : 0;
        }

        ChromePadding = new Thickness(left, 0, right, 0);
    }

    private void OnMenuClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        IsMenuOpen = !IsMenuOpen;
    }
}
