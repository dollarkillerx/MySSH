using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Views;

/// <summary>Hosts a TerminalView for one <see cref="TerminalSessionViewModel"/>; connects once the grid size is known.</summary>
public sealed partial class TerminalSessionView : UserControl, ITerminalScreen
{
    TerminalSessionViewModel? _vm;
    bool _connectStarted;

    public TerminalSessionView()
    {
        InitializeComponent();
        Term.GridSizeChanged += (_, _) =>
        {
            _vm?.NotifyGridSize(Term.Columns, Term.Rows);
            StartIfReady();
        };
        Term.TitleChanged += (_, title) => RemoteTitle.Text = title;
        Term.WorkingDirectoryChanged += (_, dir) => _vm?.NotifyWorkingDirectory(dir);
        Term.LinkActivated += async (_, uri) => { if (TopLevel.GetTopLevel(this) is IUiServices ui) await ui.OpenUriAsync(uri); };
        Term.PasteRequested += (_, e) =>
        {
            if (!e.IsMultiLine || !DesktopSettings.Current.ConfirmMultilinePaste || TopLevel.GetTopLevel(this) is not IUiServices ui) return;
            var lines = e.Text.Split('\n').Length;
            e.Defer(ui.ConfirmAsync(Loc.Instance["paste.title"], Loc.Instance.Format("paste.body", ("lines", lines)), Loc.Instance["paste.action"], danger: false));
        };
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(StartIfReady, DispatcherPriority.Background);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            if (ReferenceEquals(_vm.Screen, this)) _vm.Screen = null;
        }
        _vm = DataContext as TerminalSessionViewModel;
        if (_vm is null) return;
        _vm.Screen = this;
        _vm.PropertyChanged += OnVmChanged;
        SyncBackend();
        SyncState();
    }

    string ITerminalScreen.GetRecentText(int maxLines) => Term.GetRecentText(maxLines);
    bool ITerminalScreen.TypeText(string text, bool pressEnter) => Term.TypeText(text, pressEnter);

    void StartIfReady()
    {
        if (_vm is null || _connectStarted || Term.Columns <= 0 || Term.Rows <= 0 || !IsEffectivelyVisible) return;
        _connectStarted = true;
        _ = _vm.ConnectAsync(Term.Columns, Term.Rows);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            StartIfReady();
            Dispatcher.UIThread.Post(() => Term.FocusTerminal(), DispatcherPriority.Background);
        }
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalSessionViewModel.Backend)) SyncBackend();
        else if (e.PropertyName == nameof(TerminalSessionViewModel.State)) SyncState();
    }

    void SyncBackend()
    {
        Term.Detach();
        if (_vm?.Backend is { } backend)
        {
            Term.Attach(backend);
            Term.FocusTerminal();
        }
    }

    void SyncState()
    {
        if (_vm is null) return;
        StateDot.Fill = _vm.State switch
        {
            SessionState.Connected => this.FindResource("CarbonSupportSuccess") as IBrush,
            SessionState.Connecting => this.FindResource("CarbonSupportInfo") as IBrush,
            SessionState.Ended => this.FindResource("CarbonTextSecondary") as IBrush,
            _ => this.FindResource("CarbonSupportError") as IBrush,
        };
        Banner.Kind = _vm.BannerIsError ? MySSH.UI.Carbon.NotificationKind.Error : MySSH.UI.Carbon.NotificationKind.Info;
        Banner.Title = _vm.StateText;
    }
}
