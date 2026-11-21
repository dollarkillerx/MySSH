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

    /// <summary>Within this many pixels of the end counts as "at the bottom".</summary>
    internal const double BottomSlack = 24;

    /// <summary>
    /// Follow the newest content. True while the view is at the bottom; scrolling up to read clears it, scrolling back
    /// down (or sending, or opening a chat) sets it again.
    /// </summary>
    bool _pinned = true;

    public AiAssistantPanel()
    {
        InitializeComponent();
        // A streaming answer grows inside one item and a loaded conversation keeps growing while its Markdown is laid
        // out, so follow the content's height, not only added items.
        ChatScroll.ScrollChanged += OnChatScrolled;
    }

    void OnChatScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
        {
            if (_pinned) ChatScroll.ScrollToEnd();
        }
        else if (e.OffsetDelta.Y != 0)
        {
            _pinned = IsAtBottom;
        }
    }

    internal bool IsAtBottom => ChatScroll.Offset.Y >= ChatScroll.Extent.Height - ChatScroll.Viewport.Height - BottomSlack;

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
        {
            Dispatcher.UIThread.Post(() => AskBox.Focus(), DispatcherPriority.Background);
            FollowLatest();
        }
    }

    /// <summary>A new or reopened chat starts at its newest message.</summary>
    void Track(AiChatViewModel? chat)
    {
        if (_chat is not null) _chat.Items.CollectionChanged -= OnItemsChanged;
        _chat = chat;
        if (_chat is not null) _chat.Items.CollectionChanged += OnItemsChanged;
        FollowLatest();
    }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The user's own question always brings the view down; other items only while following.
        if (e.NewItems?.OfType<AiUserItemViewModel>().Any() == true) FollowLatest();
        else if (_pinned) ScrollToEndSoon();
    }

    void FollowLatest()
    {
        _pinned = true;
        ScrollToEndSoon();
    }

    void ScrollToEndSoon() => Dispatcher.UIThread.Post(() => { if (_pinned) ChatScroll.ScrollToEnd(); }, DispatcherPriority.Background);

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
