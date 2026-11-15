using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace MySSH.UI.Carbon;

/// <summary>Carbon notification kinds.</summary>
public enum NotificationKind
{
    Error,
    Success,
    Warning,
    Info,
}

/// <summary>
/// Carbon Inline notification: status icon, <see cref="Title"/> + <see cref="Subtitle"/>, optional
/// <see cref="Action"/> content and a close button. High contrast (inverse surface) by default;
/// <see cref="IsLowContrast"/> uses the tinted notification backgrounds.
/// </summary>
[TemplatePart("PART_CloseButton", typeof(Button))]
public class InlineNotification : ContentControl
{
    public static readonly StyledProperty<NotificationKind> KindProperty =
        AvaloniaProperty.Register<InlineNotification, NotificationKind>(nameof(Kind), NotificationKind.Info);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<InlineNotification, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<InlineNotification, string?>(nameof(Subtitle));

    public static readonly StyledProperty<bool> IsLowContrastProperty =
        AvaloniaProperty.Register<InlineNotification, bool>(nameof(IsLowContrast));

    public static readonly StyledProperty<bool> IsCloseButtonVisibleProperty =
        AvaloniaProperty.Register<InlineNotification, bool>(nameof(IsCloseButtonVisible), true);

    /// <summary>Optional action (typically a ghost <c>sm</c> button) shown before the close button.</summary>
    public static readonly StyledProperty<object?> ActionProperty =
        AvaloniaProperty.Register<InlineNotification, object?>(nameof(Action));

    /// <summary>Raised when the close button is clicked (the notification hides itself afterwards).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ClosedEvent =
        RoutedEvent.Register<InlineNotification, RoutedEventArgs>(nameof(Closed), RoutingStrategies.Bubble);

    private Button? _closeButton;

    /// <summary>Status kind (icon and accent color).</summary>
    public NotificationKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Bold title (heading-compact-01).</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Message text after the title (body-compact-01).</summary>
    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>Low-contrast variant (tinted background, primary text).</summary>
    public bool IsLowContrast
    {
        get => GetValue(IsLowContrastProperty);
        set => SetValue(IsLowContrastProperty, value);
    }

    /// <summary>Shows the close ("x") button. Default true.</summary>
    public bool IsCloseButtonVisible
    {
        get => GetValue(IsCloseButtonVisibleProperty);
        set => SetValue(IsCloseButtonVisibleProperty, value);
    }

    /// <inheritdoc cref="ActionProperty"/>
    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    /// <inheritdoc cref="ClosedEvent"/>
    public event EventHandler<RoutedEventArgs>? Closed
    {
        add => AddHandler(ClosedEvent, value);
        remove => RemoveHandler(ClosedEvent, value);
    }

    /// <summary>Hides the notification and raises <see cref="Closed"/>.</summary>
    public void Close()
    {
        IsVisible = false;
        RaiseEvent(new RoutedEventArgs(ClosedEvent, this));
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_closeButton is not null)
        {
            _closeButton.Click -= OnCloseClick;
        }

        _closeButton = e.NameScope.Find<Button>("PART_CloseButton");
        if (_closeButton is not null)
        {
            _closeButton.Click += OnCloseClick;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }
}
