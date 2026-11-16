using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MySSH.Desktop.Views;

/// <summary>Side panel around <see cref="ViewModels.HostEditorViewModel"/>; the owning page decides what Save/Cancel mean.</summary>
public sealed partial class HostEditorPanel : UserControl
{
    public static readonly RoutedEvent<RoutedEventArgs> SaveRequestedEvent =
        RoutedEvent.Register<HostEditorPanel, RoutedEventArgs>(nameof(SaveRequested), RoutingStrategies.Bubble);
    public static readonly RoutedEvent<RoutedEventArgs> CancelRequestedEvent =
        RoutedEvent.Register<HostEditorPanel, RoutedEventArgs>(nameof(CancelRequested), RoutingStrategies.Bubble);

    public HostEditorPanel() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? SaveRequested { add => AddHandler(SaveRequestedEvent, value); remove => RemoveHandler(SaveRequestedEvent, value); }
    public event EventHandler<RoutedEventArgs>? CancelRequested { add => AddHandler(CancelRequestedEvent, value); remove => RemoveHandler(CancelRequestedEvent, value); }

    void OnSave(object? sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(SaveRequestedEvent, this));
    void OnCancel(object? sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(CancelRequestedEvent, this));
}
