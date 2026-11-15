using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace MySSH.UI.Carbon;

/// <summary>Carbon tag colors.</summary>
public enum TagType
{
    Gray,
    CoolGray,
    WarmGray,
    Red,
    Magenta,
    Purple,
    Blue,
    Cyan,
    Teal,
    Green,
    HighContrast,
    Outline,
}

/// <summary>Carbon tag sizes: sm 18px, md 24px, lg 32px.</summary>
public enum TagSize
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// Carbon Tag: a pill-shaped label. <see cref="Type"/> picks the color, <see cref="IsDismissible"/> adds a close
/// button that raises <see cref="DismissedEvent"/> (used e.g. for the AI context chips).
/// </summary>
[TemplatePart("PART_DismissButton", typeof(Button))]
public class Tag : ContentControl
{
    /// <summary>Color of the tag.</summary>
    public static readonly StyledProperty<TagType> TypeProperty =
        AvaloniaProperty.Register<Tag, TagType>(nameof(Type), TagType.Gray);

    /// <summary>Size of the tag.</summary>
    public static readonly StyledProperty<TagSize> SizeProperty =
        AvaloniaProperty.Register<Tag, TagSize>(nameof(Size), TagSize.Medium);

    /// <summary>Shows the dismiss ("x") button.</summary>
    public static readonly StyledProperty<bool> IsDismissibleProperty =
        AvaloniaProperty.Register<Tag, bool>(nameof(IsDismissible));

    /// <summary>Optional leading 16px icon (see <see cref="CarbonIconNames"/>).</summary>
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<Tag, string?>(nameof(Icon));

    /// <summary>Raised when the dismiss button is clicked.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> DismissedEvent =
        RoutedEvent.Register<Tag, RoutedEventArgs>(nameof(Dismissed), RoutingStrategies.Bubble);

    private Button? _dismissButton;

    /// <inheritdoc cref="TypeProperty"/>
    public TagType Type
    {
        get => GetValue(TypeProperty);
        set => SetValue(TypeProperty, value);
    }

    /// <inheritdoc cref="SizeProperty"/>
    public TagSize Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <inheritdoc cref="IsDismissibleProperty"/>
    public bool IsDismissible
    {
        get => GetValue(IsDismissibleProperty);
        set => SetValue(IsDismissibleProperty, value);
    }

    /// <inheritdoc cref="IconProperty"/>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <inheritdoc cref="DismissedEvent"/>
    public event EventHandler<RoutedEventArgs>? Dismissed
    {
        add => AddHandler(DismissedEvent, value);
        remove => RemoveHandler(DismissedEvent, value);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_dismissButton is not null)
        {
            _dismissButton.Click -= OnDismissClick;
        }

        _dismissButton = e.NameScope.Find<Button>("PART_DismissButton");
        if (_dismissButton is not null)
        {
            _dismissButton.Click += OnDismissClick;
        }
    }

    private void OnDismissClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RaiseEvent(new RoutedEventArgs(DismissedEvent, this));
    }
}
