using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Threading;
using XTerm.Buffer;
using XKey = XTerm.Input.Key;
using XMods = XTerm.Input.KeyModifiers;

namespace TermControl;

/// <summary>
/// P0 spike: native Avalonia terminal surface on top of the XTerm.NET VT engine.
/// Engine is driven on the UI thread with a per-frame byte budget (XTerm.NET parses ~180MB/s,
/// so a 4MB/frame budget keeps frames short while a flood is being drained).
/// </summary>
public sealed class TerminalControl : Control
{
    public static readonly StyledProperty<FontFamily> TerminalFontFamilyProperty =
        AvaloniaProperty.Register<TerminalControl, FontFamily>(nameof(TerminalFontFamily),
            new FontFamily("Menlo, Monaco, Consolas, DejaVu Sans Mono, monospace"));

    public static readonly StyledProperty<double> TerminalFontSizeProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(TerminalFontSize), 14);

    public FontFamily TerminalFontFamily { get => GetValue(TerminalFontFamilyProperty); set => SetValue(TerminalFontFamilyProperty, value); }
    public double TerminalFontSize { get => GetValue(TerminalFontSizeProperty); set => SetValue(TerminalFontSizeProperty, value); }

    /// <summary>Bytes the user typed / the engine answered, to be written to the remote side.</summary>
    public event Action<byte[]>? Input;
    /// <summary>Grid size changed (cols, rows) — forward to the PTY / SSH window-change.</summary>
    public event Action<int, int>? TerminalResized;
    public event Action<string>? TitleChanged;

    public XTerm.Terminal Engine { get; }
    public TerminalPalette Palette { get; } = new();
    public bool MacOptionIsMeta { get; set; } = true;
    public int BytesPerFrameBudget { get; set; } = 4 * 1024 * 1024;

    /// <summary>Last render duration, for the spike's perf readout.</summary>
    public TimeSpan LastRenderTime { get; private set; }
    public long FramesRendered { get; private set; }

    readonly ConcurrentQueue<byte[]> _pending = new();
    readonly TerminalImeClient _ime;
    readonly DispatcherTimer _pump;
    readonly Dictionary<(string, uint, bool, bool), FormattedText> _textCache = new();
    Typeface _regular, _bold, _italic, _boldItalic;
    double _cw = 8, _ch = 16, _baseline;
    long _renderedVersion = -1;
    (int Row, int Col)? _selAnchor, _selHead; // absolute rows
    bool _selecting;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        Engine = new XTerm.Terminal(new XTerm.Options.TerminalOptions { Cols = 80, Rows = 24, Scrollback = 10000 });
        Engine.Colors.ApplyTheme(Palette.ToThemeOptions());
        Engine.DataReceived += (_, e) => Input?.Invoke(Encoding.UTF8.GetBytes(e.Data));
        Engine.TitleChanged += (_, e) => TitleChanged?.Invoke(e.Title);
        _ime = new TerminalImeClient(this);
        TextInputMethodClientRequested += (_, e) => e.Client = _ime;
        UpdateFontMetrics();
        _pump = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (_, _) => Pump());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _pump.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pump.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TerminalFontFamilyProperty || change.Property == TerminalFontSizeProperty)
        {
            UpdateFontMetrics();
            UpdateGridSize(Bounds.Size);
            InvalidateVisual();
        }
    }

    // ------------------------------------------------------------------ data in

    /// <summary>Thread-safe: queue remote output; it is parsed on the UI thread on the next tick.</summary>
    public void EnqueueOutput(ReadOnlySpan<byte> data) => _pending.Enqueue(data.ToArray());

    /// <summary>UI-thread only: parse immediately (tests / headless rendering).</summary>
    public void FeedNow(ReadOnlySpan<byte> data)
    {
        Engine.Write(data);
        AfterContentChange();
    }

    void Pump()
    {
        int budget = BytesPerFrameBudget;
        bool any = false;
        while (budget > 0 && _pending.TryDequeue(out var chunk))
        {
            Engine.Write(chunk);
            budget -= chunk.Length;
            any = true;
        }
        if (any) AfterContentChange();
    }

    void AfterContentChange()
    {
        // XTerm.NET caches ContentVersion; it must be refreshed explicitly after writes.
        if (Engine.RefreshContentVersion() != _renderedVersion) InvalidateVisual();
        _ime.NotifyCursorMoved();
    }

    // ------------------------------------------------------------------ metrics / layout

    void UpdateFontMetrics()
    {
        var fam = TerminalFontFamily;
        _regular = new Typeface(fam);
        _bold = new Typeface(fam, FontStyle.Normal, FontWeight.Bold);
        _italic = new Typeface(fam, FontStyle.Italic);
        _boldItalic = new Typeface(fam, FontStyle.Italic, FontWeight.Bold);
        var probe = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _regular, TerminalFontSize, Brushes.White);
        _cw = probe.WidthIncludingTrailingWhitespace / 10.0;
        _ch = Math.Ceiling(probe.Height);
        _baseline = probe.Baseline;
        _textCache.Clear();
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 80 * _cw : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 24 * _ch : availableSize.Height);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateGridSize(e.NewSize);
    }

    void UpdateGridSize(Size size)
    {
        int cols = Math.Max(2, (int)Math.Floor(size.Width / _cw));
        int rows = Math.Max(1, (int)Math.Floor(size.Height / _ch));
        if (cols == Engine.Cols && rows == Engine.Rows) return;
        Engine.Resize(cols, rows);
        TerminalResized?.Invoke(cols, rows);
        InvalidateVisual();
        _ime.NotifyCursorMoved();
    }

    internal Rect GetCursorRect()
    {
        var b = Engine.Buffer;
        int row = b.YBase + b.Y - b.YDisp;
        return new Rect(b.X * _cw, row * _ch, _cw, _ch);
    }

    internal void OnPreeditChanged() => InvalidateVisual();

    /// <summary>Spike/test hook: drive the same code path the platform IME uses.</summary>
    public void DebugSetPreedit(string? text) => _ime.SetPreeditText(text);

    // ------------------------------------------------------------------ rendering

    public override void Render(DrawingContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var buf = Engine.Buffer;
        var colors = Engine.Colors;
        var defaultBg = Color.FromUInt32(0xFF000000u | (uint)colors.Background);
        var defaultFg = Color.FromUInt32(0xFF000000u | (uint)colors.Foreground);
        ctx.FillRectangle(Palette.Brush(defaultBg), new Rect(Bounds.Size));

        int rows = Engine.Rows, cols = Engine.Cols;
        var sb = new StringBuilder(cols);
        for (int r = 0; r < rows; r++)
        {
            int abs = buf.YDisp + r;
            if (abs >= buf.Lines.Length) break;
            var line = buf.GetLine(abs);
            double y = r * _ch;

            // pass 1: background runs
            int runStart = 0; Color runBg = default; bool runHasBg = false;
            for (int c = 0; c <= cols; c++)
            {
                Color bg = default; bool hasBg = false;
                if (c < cols)
                {
                    var cell = line[c];
                    (_, bg, hasBg) = Resolve(cell.Attributes, defaultFg, defaultBg);
                    if (IsSelected(abs, c)) { bg = Palette.Selection; hasBg = true; }
                }
                if (c == cols || hasBg != runHasBg || (hasBg && bg != runBg))
                {
                    if (runHasBg && c > runStart)
                        ctx.FillRectangle(Palette.Brush(runBg), new Rect(runStart * _cw, y, (c - runStart) * _cw, _ch));
                    runStart = c; runBg = bg; runHasBg = hasBg;
                }
            }

            // pass 2: text. ASCII narrow cells with identical style are batched into one run
            // (the mono font's advance == cell width); anything else is drawn per cell, centred
            // in its 1- or 2-cell box so fallback fonts (CJK, emoji) can never break the grid.
            int c2 = 0;
            while (c2 < cols)
            {
                var cell = line[c2];
                if (cell.Width == 0) { c2++; continue; }
                var a = cell.Attributes;
                var content = cell.Content;
                if (string.IsNullOrEmpty(content) || content == " " || a.IsInvisible()) { c2++; continue; }
                var (fg, _, _) = Resolve(a, defaultFg, defaultBg);
                bool bold = a.IsBold(), italic = a.IsItalic();
                if (cell.Width == 1 && IsAsciiPrintable(content))
                {
                    int start = c2;
                    sb.Clear();
                    while (c2 < cols)
                    {
                        var n = line[c2];
                        if (n.Width != 1 || !IsAsciiPrintable(n.Content)) break;
                        var na = n.Attributes;
                        if (na.IsBold() != bold || na.IsItalic() != italic || Resolve(na, defaultFg, defaultBg).Fg != fg) break;
                        sb.Append(string.IsNullOrEmpty(n.Content) ? " " : n.Content);
                        c2++;
                    }
                    var ft = Text(sb.ToString().TrimEnd(), fg, bold, italic, useCache: false);
                    ctx.DrawText(ft, new Point(start * _cw, y));
                    DrawDecorations(ctx, a, fg, start, c2 - start, y);
                }
                else
                {
                    var ft = Text(content, fg, bold, italic, useCache: true);
                    double box = cell.Width * _cw;
                    double x = c2 * _cw + Math.Max(0, (box - ft.WidthIncludingTrailingWhitespace) / 2);
                    using (ctx.PushClip(new Rect(c2 * _cw, y, box, _ch)))
                        ctx.DrawText(ft, new Point(x, y + (_ch - ft.Height) / 2));
                    DrawDecorations(ctx, a, fg, c2, cell.Width, y);
                    c2 += Math.Max(1, cell.Width);
                }
            }
        }

        DrawCursorAndPreedit(ctx, defaultBg);
        _renderedVersion = Engine.ContentVersion;
        LastRenderTime = sw.Elapsed;
        FramesRendered++;
    }

    void DrawDecorations(DrawingContext ctx, AttributeData a, Color fg, int col, int width, double y)
    {
        if (a.IsUnderline())
            ctx.FillRectangle(Palette.Brush(fg), new Rect(col * _cw, y + _baseline + 1.5, width * _cw, 1));
        if (a.IsStrikethrough())
            ctx.FillRectangle(Palette.Brush(fg), new Rect(col * _cw, y + _ch / 2, width * _cw, 1));
    }

    void DrawCursorAndPreedit(DrawingContext ctx, Color defaultBg)
    {
        var b = Engine.Buffer;
        int row = b.YBase + b.Y - b.YDisp;
        if (row < 0 || row >= Engine.Rows) return;
        var rect = new Rect(b.X * _cw, row * _ch, _cw, _ch);

        var preedit = _ime.Preedit;
        if (preedit != null)
        {
            // inline composition: drawn over the grid at the cursor, underlined (Carbon blue 50)
            var ft = Text(preedit, Palette.Foreground, false, false, useCache: false);
            var w = Math.Max(_cw, ft.WidthIncludingTrailingWhitespace);
            var pr = new Rect(rect.X, rect.Y, w, _ch);
            ctx.FillRectangle(Palette.Brush(Palette.PreeditBackground), pr);
            ctx.DrawText(ft, new Point(pr.X, pr.Y + (_ch - ft.Height) / 2));
            ctx.FillRectangle(Palette.Brush(Palette.PreeditUnderline), new Rect(pr.X, pr.Bottom - 2, w, 2));
            ctx.FillRectangle(Palette.Brush(Palette.Cursor), new Rect(pr.Right, pr.Y, 2, _ch));
            return;
        }
        if (!Engine.CursorVisible) return;

        var line = b.GetLine(b.YBase + b.Y);
        int width = b.X < Engine.Cols ? Math.Max(1, line.GetWidth(b.X)) : 1;
        rect = rect.WithWidth(width * _cw);
        if (IsFocused)
        {
            ctx.FillRectangle(Palette.Brush(Palette.Cursor), rect);
            if (b.X < Engine.Cols)
            {
                var content = line[b.X].Content;
                if (!string.IsNullOrWhiteSpace(content))
                {
                    var ft = Text(content, defaultBg, false, false, useCache: true);
                    ctx.DrawText(ft, new Point(rect.X + Math.Max(0, (rect.Width - ft.WidthIncludingTrailingWhitespace) / 2), rect.Y + (_ch - ft.Height) / 2));
                }
            }
        }
        else
        {
            ctx.DrawRectangle(new Pen(Palette.Brush(Palette.Cursor), 1), rect.Deflate(0.5));
        }
    }

    static bool IsAsciiPrintable(string? s) => string.IsNullOrEmpty(s) || (s.Length == 1 && s[0] >= 0x20 && s[0] < 0x7f);

    (Color Fg, Color Bg, bool HasBg) Resolve(AttributeData a, Color defaultFg, Color defaultBg)
    {
        Color fg = a.GetFgColorKind() switch
        {
            XTerm.Common.ColorMode.RGB => Color.FromUInt32(0xFF000000u | (uint)a.GetFgColor()),
            XTerm.Common.ColorMode.Palette16 or XTerm.Common.ColorMode.Palette256 => Palette.Indexed(a.GetFgColor() & 0xff),
            _ => defaultFg,
        };
        bool hasBg = a.GetBgColorKind() != XTerm.Common.ColorMode.Default;
        Color bg = a.GetBgColorKind() switch
        {
            XTerm.Common.ColorMode.RGB => Color.FromUInt32(0xFF000000u | (uint)a.GetBgColor()),
            XTerm.Common.ColorMode.Palette16 or XTerm.Common.ColorMode.Palette256 => Palette.Indexed(a.GetBgColor() & 0xff),
            _ => defaultBg,
        };
        if (a.IsBold() && a.GetFgColorKind() == XTerm.Common.ColorMode.Palette16 && (a.GetFgColor() & 0xff) < 8)
            fg = Palette.Indexed((a.GetFgColor() & 0xff) + 8);
        if (a.IsInverse()) { (fg, bg) = (bg, fg); hasBg = true; }
        if (a.IsDim()) fg = Color.FromArgb(0xA0, fg.R, fg.G, fg.B);
        return (fg, bg, hasBg);
    }

    FormattedText Text(string s, Color fg, bool bold, bool italic, bool useCache)
    {
        var key = (s, fg.ToUInt32(), bold, italic);
        if (useCache && _textCache.TryGetValue(key, out var cached)) return cached;
        var tf = (bold, italic) switch { (true, true) => _boldItalic, (true, _) => _bold, (_, true) => _italic, _ => _regular };
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, TerminalFontSize, Palette.Brush(fg));
        if (useCache)
        {
            if (_textCache.Count > 8192) _textCache.Clear();
            _textCache[key] = ft;
        }
        return ft;
    }

    // ------------------------------------------------------------------ keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key == Key.ImeProcessed || _ime.Preedit != null) return;

        bool isMac = OperatingSystem.IsMacOS();
        var platformCmd = isMac ? KeyModifiers.Meta : KeyModifiers.Control | KeyModifiers.Shift;
        if (e.KeyModifiers == platformCmd && e.Key == Key.C) { _ = CopySelectionAsync(); e.Handled = true; return; }
        if (e.KeyModifiers == platformCmd && e.Key == Key.V) { _ = PasteAsync(); e.Handled = true; return; }
        if (isMac && e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return; // leave other Cmd shortcuts to the app

        var mods = XMods.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) mods |= XMods.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) mods |= XMods.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) mods |= XMods.Control;

        string? seq = null;
        if (MapSpecial(e.Key) is { } xk)
        {
            seq = Engine.GenerateKeyInput(xk, mods);
        }
        else if (mods.HasFlag(XMods.Control) || (mods.HasFlag(XMods.Alt) && (!isMac || MacOptionIsMeta)))
        {
            // Ctrl+letter / Ctrl+[ ... and Alt(Option)-as-Meta: the platform would not give us usable TextInput
            char? ch = KeyToChar(e.Key, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            if (ch is { } c) seq = Engine.GenerateCharInput(c, mods);
        }
        if (seq is { Length: > 0 })
        {
            ScrollToBottomOnInput();
            Input?.Invoke(Encoding.UTF8.GetBytes(seq));
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text)) return;
        var text = e.Text;
        if (text.All(char.IsControl)) return; // Enter/Tab/BS already encoded in OnKeyDown
        ScrollToBottomOnInput();
        Input?.Invoke(Encoding.UTF8.GetBytes(text)); // committed IME text lands here as well
        e.Handled = true;
    }

    void ScrollToBottomOnInput()
    {
        ClearSelection();
        if (!Engine.Buffer.IsAtBottom) { Engine.ScrollToBottom(); InvalidateVisual(); }
    }

    static XKey? MapSpecial(Key k) => k switch
    {
        Key.Enter => XKey.Enter, Key.Tab => XKey.Tab, Key.Back => XKey.Backspace, Key.Escape => XKey.Escape,
        Key.Up => XKey.UpArrow, Key.Down => XKey.DownArrow, Key.Left => XKey.LeftArrow, Key.Right => XKey.RightArrow,
        Key.Home => XKey.Home, Key.End => XKey.End, Key.PageUp => XKey.PageUp, Key.PageDown => XKey.PageDown,
        Key.Insert => XKey.Insert, Key.Delete => XKey.Delete,
        Key.F1 => XKey.F1, Key.F2 => XKey.F2, Key.F3 => XKey.F3, Key.F4 => XKey.F4, Key.F5 => XKey.F5, Key.F6 => XKey.F6,
        Key.F7 => XKey.F7, Key.F8 => XKey.F8, Key.F9 => XKey.F9, Key.F10 => XKey.F10, Key.F11 => XKey.F11, Key.F12 => XKey.F12,
        _ => null,
    };

    static char? KeyToChar(Key k, bool shift)
    {
        if (k >= Key.A && k <= Key.Z) return (char)((shift ? 'A' : 'a') + (k - Key.A));
        if (k >= Key.D0 && k <= Key.D9) return (char)('0' + (k - Key.D0));
        return k switch
        {
            Key.Space => ' ', Key.OemOpenBrackets => '[', Key.OemCloseBrackets => ']', Key.OemPipe => '\\',
            Key.OemMinus => '-', Key.OemPlus => '=', Key.OemPeriod => '.', Key.OemComma => ',', Key.OemQuestion => '/',
            Key.OemSemicolon => ';', Key.OemQuotes => '\'', Key.OemTilde => '`',
            _ => null,
        };
    }

    // ------------------------------------------------------------------ mouse / selection / clipboard

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var (col, row) = CellAt(e.GetPosition(this));
        var props = e.GetCurrentPoint(this).Properties;
        bool appMouse = Engine.MouseTrackingMode != XTerm.Input.MouseTrackingMode.None && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (appMouse)
        {
            var btn = props.IsRightButtonPressed ? XTerm.Input.MouseButton.Right : props.IsMiddleButtonPressed ? XTerm.Input.MouseButton.Middle : XTerm.Input.MouseButton.Left;
            SendMouse(btn, col, row, XTerm.Input.MouseEventType.Down, e.KeyModifiers);
            e.Handled = true;
            return;
        }
        if (props.IsLeftButtonPressed)
        {
            int abs = Engine.Buffer.YDisp + row;
            if (e.ClickCount == 2) SelectWord(abs, col);
            else if (e.ClickCount >= 3) { _selAnchor = (abs, 0); _selHead = (abs, Engine.Cols - 1); }
            else { _selAnchor = _selHead = (abs, col); _selecting = true; }
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting) return;
        var (col, row) = CellAt(e.GetPosition(this));
        _selHead = (Engine.Buffer.YDisp + row, col);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Engine.MouseTrackingMode != XTerm.Input.MouseTrackingMode.None && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var (col, row) = CellAt(e.GetPosition(this));
            SendMouse(XTerm.Input.MouseButton.Left, col, row, XTerm.Input.MouseEventType.Up, e.KeyModifiers);
        }
        _selecting = false;
        if (_selAnchor == _selHead) ClearSelection();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        int lines = (int)Math.Round(-e.Delta.Y * 3);
        if (lines == 0) return;
        var (col, row) = CellAt(e.GetPosition(this));
        if (Engine.MouseTrackingMode != XTerm.Input.MouseTrackingMode.None)
        {
            var btn = lines < 0 ? XTerm.Input.MouseButton.WheelUp : XTerm.Input.MouseButton.WheelDown;
            var type = lines < 0 ? XTerm.Input.MouseEventType.WheelUp : XTerm.Input.MouseEventType.WheelDown;
            for (int i = 0; i < Math.Abs(lines); i++) SendMouse(btn, col, row, type, e.KeyModifiers);
        }
        else if (Engine.IsAlternateBufferActive)
        {
            var seq = Engine.GenerateKeyInput(lines < 0 ? XKey.UpArrow : XKey.DownArrow, XMods.None);
            Input?.Invoke(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(seq, Math.Abs(lines)))));
        }
        else
        {
            Engine.ScrollLines(lines);
            InvalidateVisual();
            _ime.NotifyCursorMoved();
        }
        e.Handled = true;
    }

    void SendMouse(XTerm.Input.MouseButton btn, int col, int row, XTerm.Input.MouseEventType type, KeyModifiers km)
    {
        var mods = XMods.None;
        if (km.HasFlag(KeyModifiers.Shift)) mods |= XMods.Shift;
        if (km.HasFlag(KeyModifiers.Alt)) mods |= XMods.Alt;
        if (km.HasFlag(KeyModifiers.Control)) mods |= XMods.Control;
        var seq = Engine.GenerateMouseEvent(btn, col, row, type, mods);
        if (!string.IsNullOrEmpty(seq)) Input?.Invoke(Encoding.UTF8.GetBytes(seq));
    }

    (int Col, int Row) CellAt(Point p) =>
        (Math.Clamp((int)(p.X / _cw), 0, Engine.Cols - 1), Math.Clamp((int)(p.Y / _ch), 0, Engine.Rows - 1));

    void SelectWord(int abs, int col)
    {
        var line = Engine.Buffer.GetLine(abs);
        bool IsWord(int c) => c >= 0 && c < Engine.Cols && line[c].Content is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s) && !"()[]{}'\",;|<>".Contains(s[0]);
        int s0 = col, e0 = col;
        while (IsWord(s0 - 1)) s0--;
        while (IsWord(e0 + 1)) e0++;
        _selAnchor = (abs, s0); _selHead = (abs, e0);
    }

    bool IsSelected(int abs, int col)
    {
        if (_selAnchor is not { } a || _selHead is not { } h) return false;
        var (s, e) = Compare(a, h) <= 0 ? (a, h) : (h, a);
        return Compare((abs, col), s) >= 0 && Compare((abs, col), e) <= 0;
    }

    static int Compare((int Row, int Col) x, (int Row, int Col) y) => x.Row != y.Row ? x.Row.CompareTo(y.Row) : x.Col.CompareTo(y.Col);

    void ClearSelection()
    {
        if (_selAnchor is null) return;
        _selAnchor = _selHead = null;
        InvalidateVisual();
    }

    public string? GetSelectionText()
    {
        if (_selAnchor is not { } a || _selHead is not { } h) return null;
        var (s, e) = Compare(a, h) <= 0 ? (a, h) : (h, a);
        var sb = new StringBuilder();
        for (int r = s.Row; r <= e.Row; r++)
        {
            var line = Engine.Buffer.GetLine(r);
            int from = r == s.Row ? s.Col : 0, to = r == e.Row ? e.Col + 1 : Engine.Cols;
            sb.Append(line.TranslateToString(true, from, to));
            if (r != e.Row && !Engine.Buffer.GetLine(r + 1).IsWrapped) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Visible text for AI/MCP read_terminal (design §4.3).</summary>
    public string GetVisibleText(int maxLines)
    {
        var lines = Engine.GetVisibleLines();
        return string.Join('\n', lines.TakeLast(maxLines).Select(l => l.TrimEnd()));
    }

    async Task CopySelectionAsync()
    {
        var text = GetSelectionText();
        if (text is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
        await cb.SetTextAsync(text);
    }

    async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
        var text = await cb.TryGetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        ScrollToBottomOnInput();
        Engine.Paste(text); // emits bracketed-paste wrapped data through DataReceived when mode 2004 is on
    }
}
