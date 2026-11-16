using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using XKey = XTerm.Input.Key;
using XMods = XTerm.Input.KeyModifiers;
using XMouse = XTerm.Input.MouseButton;
using XMouseEvent = XTerm.Input.MouseEventType;

namespace MySSH.Terminal;

public sealed partial class TerminalView
{
    private readonly TerminalSelection _selection = new();
    private bool _selecting;
    private bool _textInputSuppressed;
    private LinkHit? _hoverLink;
    private double _wheelAccumulator;
    private (int Col, int Row) _lastMouseCell = (-1, -1);
    private XMouse _mouseButtonDown = XMouse.None;

    /// <summary>True when text is selected.</summary>
    public bool HasSelection => _selection.HasSelection;

    internal static bool IsMac { get; set; } = OperatingSystem.IsMacOS();

    // ------------------------------------------------------------------ keyboard

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _textInputSuppressed = false;
        if (e.Handled || e.Key == Key.ImeProcessed || _ime.Preedit is not null)
        {
            return; // the IME owns the key while composing
        }

        var mods = e.KeyModifiers;
        var copyPaste = IsMac ? KeyModifiers.Meta : KeyModifiers.Control | KeyModifiers.Shift;
        if (mods == copyPaste && e.Key == Key.C)
        {
            _ = CopySelectionAsync();
            e.Handled = true;
            return;
        }

        if ((mods == copyPaste && e.Key == Key.V) || (mods == KeyModifiers.Shift && e.Key == Key.Insert))
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (IsMac && mods == KeyModifiers.Meta && e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
            return;
        }

        // Shift+PageUp/PageDown/Home/End scroll the local scrollback on the main screen
        if (mods == KeyModifiers.Shift && !Engine.IsAlternateBufferActive && e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            switch (e.Key)
            {
                case Key.PageUp: ScrollLines(-Math.Max(1, Engine.Rows - 1)); break;
                case Key.PageDown: ScrollLines(Math.Max(1, Engine.Rows - 1)); break;
                case Key.Home: Engine.ScrollToTop(); RefreshRows(); break;
                default: ScrollToBottom(); break;
            }

            e.Handled = true;
            return;
        }

        if (_inputClosed)
        {
            return;
        }

        var seq = KeyEncoder.Encode(Engine, e.Key, mods, e.KeySymbol, IsMac, MacOptionIsMeta, out var characterCombo);
        if (seq is not null)
        {
            ScrollToBottomOnInput();
            SendText(seq);
            _textInputSuppressed = characterCombo; // the platform may still deliver TextInput for it (Option+b → "∫")
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        var suppressed = _textInputSuppressed;
        _textInputSuppressed = false;
        if (e.Handled || string.IsNullOrEmpty(e.Text) || _inputClosed)
        {
            return;
        }

        e.Handled = true;
        if (suppressed || e.Text.All(char.IsControl))
        {
            return; // already encoded in OnKeyDown (Enter/Tab/Backspace, Meta combos)
        }

        ScrollToBottomOnInput();
        SendText(e.Text); // committed IME text lands here as well
    }

    /// <inheritdoc />
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (Engine.SendFocusEvents)
        {
            SendText(Engine.GenerateFocusEvent(true));
        }

        RestartBlink();
    }

    /// <inheritdoc />
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (Engine.SendFocusEvents)
        {
            SendText(Engine.GenerateFocusEvent(false));
        }

        SetHoverLink(null);
        RestartBlink();
    }

    private void ScrollToBottomOnInput()
    {
        if (_selection.HasSelection && !_selecting)
        {
            ClearSelection();
        }

        ScrollToBottom();
        _blinkOn = true;
    }

    // ------------------------------------------------------------------ pointer

    private bool AppMouse(KeyModifiers mods) =>
        Engine.MouseTrackingMode != XTerm.Input.MouseTrackingMode.None && !mods.HasFlag(KeyModifiers.Shift) && !_inputClosed;

    private static bool LinkModifier(KeyModifiers mods) => mods.HasFlag(IsMac ? KeyModifiers.Meta : KeyModifiers.Control);

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_ime.Preedit is not null || Fonts is null)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var (col, row) = CellAt(pos);
        var point = e.GetCurrentPoint(this);
        var props = point.Properties;

        if (props.IsLeftButtonPressed && LinkModifier(e.KeyModifiers) && FindLink(col, row) is { } link)
        {
            LinkActivated?.Invoke(this, link.Uri);
            e.Handled = true;
            return;
        }

        if (AppMouse(e.KeyModifiers))
        {
            _mouseButtonDown = props.IsRightButtonPressed ? XMouse.Right : props.IsMiddleButtonPressed ? XMouse.Middle : XMouse.Left;
            SendMouse(_mouseButtonDown, col, row, XMouseEvent.Down, e.KeyModifiers);
            _lastMouseCell = (col, row);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        var abs = Engine.Buffer.YDisp + row;
        var trimmed = Engine.Buffer.TrimmedLines;
        if (e.ClickCount == 1 && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selection.HasSelection)
        {
            _selection.Extend(Engine.Buffer, trimmed, abs, col, Engine.Cols);
        }
        else
        {
            var kind = e.ClickCount switch
            {
                2 => SelectionKind.Word,
                >= 3 => SelectionKind.Line,
                _ => e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? SelectionKind.Block : SelectionKind.Character,
            };
            _selection.Start(Engine.Buffer, trimmed, abs, col, kind, Engine.Cols);
        }

        _selecting = true;
        e.Pointer.Capture(this);
        OnSelectionChanged();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Fonts is null)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var (col, row) = CellAt(pos);

        if (_selecting)
        {
            // drag above/below the viewport scrolls the scrollback
            if (pos.Y < 0)
            {
                ScrollLines(-1);
            }
            else if (pos.Y > Bounds.Height)
            {
                ScrollLines(1);
            }

            _selection.Extend(Engine.Buffer, Engine.Buffer.TrimmedLines, Engine.Buffer.YDisp + row, col, Engine.Cols);
            OnSelectionChanged();
            return;
        }

        if (AppMouse(e.KeyModifiers) && (col, row) != _lastMouseCell)
        {
            var mode = Engine.MouseTrackingMode;
            if (_mouseButtonDown != XMouse.None && mode is XTerm.Input.MouseTrackingMode.ButtonEvent or XTerm.Input.MouseTrackingMode.AnyEvent)
            {
                SendMouse(_mouseButtonDown, col, row, XMouseEvent.Drag, e.KeyModifiers);
            }
            else if (mode == XTerm.Input.MouseTrackingMode.AnyEvent)
            {
                SendMouse(XMouse.None, col, row, XMouseEvent.Move, e.KeyModifiers);
            }

            _lastMouseCell = (col, row);
        }

        SetHoverLink(LinkModifier(e.KeyModifiers) ? FindLink(col, row) : null);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Fonts is null)
        {
            return;
        }

        if (_mouseButtonDown != XMouse.None)
        {
            var (col, row) = CellAt(e.GetPosition(this));
            var button = e.InitialPressMouseButton switch
            {
                MouseButton.Right => XMouse.Right,
                MouseButton.Middle => XMouse.Middle,
                _ => XMouse.Left,
            };
            SendMouse(button, col, row, XMouseEvent.Up, e.KeyModifiers);
            _mouseButtonDown = XMouse.None;
        }

        if (_selecting)
        {
            _selecting = false;
            if (_selection.Kind == SelectionKind.Character && _selection.Anchor == _selection.Head)
            {
                ClearSelection();
            }
        }

        e.Pointer.Capture(null);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Fonts is null)
        {
            return;
        }

        _wheelAccumulator += -e.Delta.Y * 3;
        var lines = (int)_wheelAccumulator;
        if (lines == 0)
        {
            e.Handled = true;
            return;
        }

        _wheelAccumulator -= lines;
        var (col, row) = CellAt(e.GetPosition(this));
        if (AppMouse(e.KeyModifiers))
        {
            var button = lines < 0 ? XMouse.WheelUp : XMouse.WheelDown;
            var type = lines < 0 ? XMouseEvent.WheelUp : XMouseEvent.WheelDown;
            for (var i = 0; i < Math.Abs(lines); i++)
            {
                SendMouse(button, col, row, type, e.KeyModifiers);
            }
        }
        else if (Engine.IsAlternateBufferActive && !_inputClosed)
        {
            // full-screen apps without mouse mode (less, man): wheel = arrow keys (honours DECCKM)
            var seq = Engine.GenerateKeyInput(lines < 0 ? XKey.UpArrow : XKey.DownArrow, XMods.None);
            SendText(string.Concat(Enumerable.Repeat(seq, Math.Abs(lines))));
        }
        else
        {
            ScrollLines(lines);
        }

        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_hoverLink is not null && !LinkModifier(e.KeyModifiers))
        {
            SetHoverLink(null);
        }
    }

    private void SendMouse(XMouse button, int col, int row, XMouseEvent type, KeyModifiers km)
    {
        var mods = XMods.None;
        if (km.HasFlag(KeyModifiers.Shift)) mods |= XMods.Shift;
        if (km.HasFlag(KeyModifiers.Alt)) mods |= XMods.Alt;
        if (km.HasFlag(KeyModifiers.Control)) mods |= XMods.Control;
        SendText(Engine.GenerateMouseEvent(button, col, row, type, mods));
    }

    internal (int Col, int Row) CellAt(Point p)
    {
        var f = Fonts!;
        return (Math.Clamp((int)(p.X / f.CellWidth), 0, Engine.Cols - 1), Math.Clamp((int)(p.Y / f.CellHeight), 0, Engine.Rows - 1));
    }

    // ------------------------------------------------------------------ links

    /// <summary>Link (OSC 8 or URL text) at a viewport cell, or null.</summary>
    internal LinkHit? FindLink(int col, int viewportRow) =>
        LinkDetector.Find(Engine.Buffer, Engine.Buffer.YDisp + viewportRow, col, Engine.Cols);

    private void SetHoverLink(LinkHit? link)
    {
        if (link == _hoverLink || (link is not null && _hoverLink is not null && link.Uri == _hoverLink.Uri && link.Segments.SequenceEqual(_hoverLink.Segments)))
        {
            return;
        }

        _hoverLink = link;
        Cursor = new Cursor(link is null ? StandardCursorType.Ibeam : StandardCursorType.Hand);
        RefreshRows();
    }

    // ------------------------------------------------------------------ selection & clipboard

    /// <summary>The selected text ('\n' between lines, soft wraps joined), or null.</summary>
    public string? GetSelectionText() => _selection.GetText(Engine.Buffer, Engine.Buffer.TrimmedLines, Engine.Cols);

    /// <summary>Selects the whole buffer (scrollback and screen).</summary>
    public void SelectAll()
    {
        _selection.SelectAll(Engine.Buffer, Engine.Buffer.TrimmedLines, Engine.Cols);
        OnSelectionChanged();
    }

    /// <summary>Removes the selection.</summary>
    public void ClearSelection()
    {
        if (_selection.HasSelection)
        {
            _selection.Clear();
            OnSelectionChanged();
        }
    }

    /// <summary>Test hook: select a range in viewport coordinates.</summary>
    internal void Select(int startCol, int startRow, int endCol, int endRow, SelectionKind kind = SelectionKind.Character)
    {
        var buf = Engine.Buffer;
        _selection.Start(buf, buf.TrimmedLines, buf.YDisp + startRow, startCol, kind, Engine.Cols);
        _selection.Extend(buf, buf.TrimmedLines, buf.YDisp + endRow, endCol, Engine.Cols);
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        RefreshRows();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copies the selection to the clipboard. Returns false when nothing is selected or there is no clipboard.</summary>
    public async Task<bool> CopySelectionAsync()
    {
        var text = GetSelectionText();
        if (string.IsNullOrEmpty(text) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        await clipboard.SetTextAsync(text);
        return true;
    }

    /// <summary>Pastes the clipboard text (through <see cref="PasteRequested"/>).</summary>
    public async Task<bool> PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        var text = await clipboard.TryGetTextAsync();
        return !string.IsNullOrEmpty(text) && await PasteTextAsync(text);
    }

    /// <summary>
    /// Pastes <paramref name="text"/> as the user would: raises <see cref="PasteRequested"/> (which may confirm,
    /// rewrite or cancel), then sends it through the engine, which wraps it in bracketed-paste markers when the
    /// application enabled mode 2004 and normalises line breaks to CR.
    /// </summary>
    public async Task<bool> PasteTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_inputClosed || _backend is null)
        {
            return false;
        }

        var args = new TerminalPasteEventArgs(text) { IsBracketed = Engine.BracketedPasteMode };
        PasteRequested?.Invoke(this, args);
        if (args.Confirmation is { } confirm && !await confirm)
        {
            return false;
        }

        if (args.Cancel || string.IsNullOrEmpty(args.Text) || _inputClosed)
        {
            return false;
        }

        ScrollToBottomOnInput();
        Engine.Paste(args.Text);
        return true;
    }
}
