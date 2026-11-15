using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MySSH.UI.Carbon;

/// <summary>Carbon modal widths: xs 368, sm 448, md 640 (default), lg 960 (each capped to 90% of the window).</summary>
public enum ModalSize
{
    ExtraSmall,
    Small,
    Medium,
    Large,
}

/// <summary>How a <see cref="Modal"/> was closed.</summary>
public enum ModalResult
{
    /// <summary>Close button, Escape, or <see cref="Modal.Close"/>.</summary>
    None,
    Primary,
    Secondary,
}

/// <summary>Event args of <see cref="Modal.ClosedEvent"/>.</summary>
public sealed class ModalClosedEventArgs(RoutedEvent routedEvent, object source, ModalResult result)
    : RoutedEventArgs(routedEvent, source)
{
    public ModalResult Result { get; } = result;
}

/// <summary>
/// Carbon Modal: a full-window overlay (overlay token) with a centered dialog: <see cref="Label"/>,
/// <see cref="Heading"/>, close button, body (<see cref="ContentControl.Content"/>) and a footer with secondary /
/// primary buttons. <see cref="IsDanger"/> makes the primary button a danger button.
/// Either place it in the window (spanning the whole client area) and toggle <see cref="IsOpen"/>, or call
/// <see cref="ShowAsync"/> which hosts it in the window's overlay layer.
/// </summary>
[TemplatePart("PART_CloseButton", typeof(Button))]
[TemplatePart("PART_PrimaryButton", typeof(Button))]
[TemplatePart("PART_SecondaryButton", typeof(Button))]
public class Modal : ContentControl
{
    public static readonly StyledProperty<string?> HeadingProperty =
        AvaloniaProperty.Register<Modal, string?>(nameof(Heading));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<Modal, string?>(nameof(Label));

    public static readonly StyledProperty<string?> PrimaryButtonTextProperty =
        AvaloniaProperty.Register<Modal, string?>(nameof(PrimaryButtonText), "OK");

    public static readonly StyledProperty<string?> SecondaryButtonTextProperty =
        AvaloniaProperty.Register<Modal, string?>(nameof(SecondaryButtonText), "Cancel");

    public static readonly StyledProperty<bool> IsPrimaryButtonEnabledProperty =
        AvaloniaProperty.Register<Modal, bool>(nameof(IsPrimaryButtonEnabled), true);

    public static readonly StyledProperty<bool> IsDangerProperty =
        AvaloniaProperty.Register<Modal, bool>(nameof(IsDanger));

    public static readonly StyledProperty<ModalSize> SizeProperty =
        AvaloniaProperty.Register<Modal, ModalSize>(nameof(Size), ModalSize.Medium);

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<Modal, bool>(nameof(IsOpen));

    /// <summary>False hides the close button and ignores Esc (e.g. a required unlock prompt). Default true.</summary>
    public static readonly StyledProperty<bool> IsDismissibleProperty =
        AvaloniaProperty.Register<Modal, bool>(nameof(IsDismissible), true);

    /// <summary>Raised when the primary button is clicked. Set Handled to keep the modal open.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> PrimaryClickEvent =
        RoutedEvent.Register<Modal, RoutedEventArgs>(nameof(PrimaryClick), RoutingStrategies.Bubble);

    /// <summary>Raised when the secondary button is clicked. Set Handled to keep the modal open.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> SecondaryClickEvent =
        RoutedEvent.Register<Modal, RoutedEventArgs>(nameof(SecondaryClick), RoutingStrategies.Bubble);

    /// <summary>Raised after the modal closed.</summary>
    public static readonly RoutedEvent<ModalClosedEventArgs> ClosedEvent =
        RoutedEvent.Register<Modal, ModalClosedEventArgs>(nameof(Closed), RoutingStrategies.Bubble);

    private Button? _close;
    private Button? _primary;
    private Button? _secondary;

    /// <summary>Dialog title (heading-03).</summary>
    public string? Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    /// <summary>Small label above the heading (label-01).</summary>
    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Primary button text; null hides the button.</summary>
    public string? PrimaryButtonText
    {
        get => GetValue(PrimaryButtonTextProperty);
        set => SetValue(PrimaryButtonTextProperty, value);
    }

    /// <summary>Secondary button text; null hides the button.</summary>
    public string? SecondaryButtonText
    {
        get => GetValue(SecondaryButtonTextProperty);
        set => SetValue(SecondaryButtonTextProperty, value);
    }

    public bool IsPrimaryButtonEnabled
    {
        get => GetValue(IsPrimaryButtonEnabledProperty);
        set => SetValue(IsPrimaryButtonEnabledProperty, value);
    }

    /// <summary>Danger modal: the primary button uses the danger kind.</summary>
    public bool IsDanger
    {
        get => GetValue(IsDangerProperty);
        set => SetValue(IsDangerProperty, value);
    }

    public ModalSize Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Whether the modal (and its overlay) is shown.</summary>
    /// <inheritdoc cref="IsDismissibleProperty"/>
    public bool IsDismissible
    {
        get => GetValue(IsDismissibleProperty);
        set => SetValue(IsDismissibleProperty, value);
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? PrimaryClick
    {
        add => AddHandler(PrimaryClickEvent, value);
        remove => RemoveHandler(PrimaryClickEvent, value);
    }

    public event EventHandler<RoutedEventArgs>? SecondaryClick
    {
        add => AddHandler(SecondaryClickEvent, value);
        remove => RemoveHandler(SecondaryClickEvent, value);
    }

    public event EventHandler<ModalClosedEventArgs>? Closed
    {
        add => AddHandler(ClosedEvent, value);
        remove => RemoveHandler(ClosedEvent, value);
    }

    /// <summary>Closes the modal with <paramref name="result"/> and raises <see cref="Closed"/>.</summary>
    public void Close(ModalResult result = ModalResult.None)
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        RaiseEvent(new ModalClosedEventArgs(ClosedEvent, this, result));
    }

    /// <summary>
    /// Shows the modal in the overlay layer of <paramref name="anchor"/>'s window and completes when it closes.
    /// </summary>
    public Task<ModalResult> ShowAsync(Visual anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var layer = OverlayLayer.GetOverlayLayer(anchor)
            ?? throw new InvalidOperationException("The anchor is not attached to a window with an overlay layer.");
        var tcs = new TaskCompletionSource<ModalResult>();

        void OnClosed(object? sender, ModalClosedEventArgs e)
        {
            Closed -= OnClosed;
            layer.Children.Remove(this);
            tcs.TrySetResult(e.Result);
        }

        Closed += OnClosed;
        layer.Children.Add(this);
        IsOpen = true;
        return tcs.Task;
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        Unhook();
        _close = e.NameScope.Find<Button>("PART_CloseButton");
        _primary = e.NameScope.Find<Button>("PART_PrimaryButton");
        _secondary = e.NameScope.Find<Button>("PART_SecondaryButton");
        if (_close is not null)
        {
            _close.Click += OnCloseClick;
        }

        if (_primary is not null)
        {
            _primary.Click += OnPrimaryClick;
        }

        if (_secondary is not null)
        {
            _secondary.Click += OnSecondaryClick;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty && change.GetNewValue<bool>())
        {
            Dispatcher.UIThread.Post(() => (_primary is { IsEffectivelyVisible: true, IsEnabled: true } ? _primary : (Control?)_secondary ?? this).Focus());
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && IsOpen && IsDismissible)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Unhook()
    {
        if (_close is not null)
        {
            _close.Click -= OnCloseClick;
        }

        if (_primary is not null)
        {
            _primary.Click -= OnPrimaryClick;
        }

        if (_secondary is not null)
        {
            _secondary.Click -= OnSecondaryClick;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var args = new RoutedEventArgs(PrimaryClickEvent, this);
        RaiseEvent(args);
        if (!args.Handled)
        {
            Close(ModalResult.Primary);
        }
    }

    private void OnSecondaryClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var args = new RoutedEventArgs(SecondaryClickEvent, this);
        RaiseEvent(args);
        if (!args.Handled)
        {
            Close(ModalResult.Secondary);
        }
    }
}
