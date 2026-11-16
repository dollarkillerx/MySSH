using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MySSH.Desktop.ViewModels;
using MySSH.UI.Carbon;

namespace MySSH.Desktop.Views;

public sealed partial class AiAssistantPanel : UserControl
{
    AiChatViewModel? _chat;
    AiAssistantViewModel? _vm;

    public AiAssistantPanel()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as AiAssistantViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        Track(_vm?.Current);
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiAssistantViewModel.Current)) Track(_vm?.Current);
        else if (e.PropertyName == nameof(AiAssistantViewModel.IsOpen) && _vm?.IsOpen == true)
            Dispatcher.UIThread.Post(() => AskBox.Focus(), DispatcherPriority.Background);
    }

    /// <summary>Keeps the newest message in view while a chat streams.</summary>
    void Track(AiChatViewModel? chat)
    {
        if (_chat is not null) _chat.Items.CollectionChanged -= OnItemsChanged;
        _chat = chat;
        if (_chat is not null) _chat.Items.CollectionChanged += OnItemsChanged;
        ScrollToEnd();
    }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEnd();

    void ScrollToEnd() => Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);

    void OnChipDismissed(object? sender, RoutedEventArgs e)
    {
        if (sender is Tag { DataContext: AiContextChipViewModel chip }) chip.RemoveCommand.Execute(null);
    }

    /// <summary>Enter sends, Shift+Enter starts a new line.</summary>
    void OnAskKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || _chat is null) return;
        e.Handled = true;
        if (_chat.CanSend) _chat.SendCommand.Execute(null);
    }
}
