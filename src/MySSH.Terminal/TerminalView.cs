using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MySSH.Core;
using MySSH.Terminal.Rendering;
using XTerm.Options;

namespace MySSH.Terminal;

/// <summary>
/// Native terminal control: XTerm.NET VT engine + per-row cached rendering, xterm keyboard/mouse encoding,
/// IME composition, selection, search and links. Feed it with <see cref="Attach"/>; everything else is UI.
/// </summary>
/// <remarks>
/// Threading: the backend's <see cref="ITerminalBackend.DataReceived"/> may fire on any thread; data is queued
/// and parsed on the UI thread with a per-slice byte/time budget, so a flood never freezes input. All public
/// members must be called on the UI thread.
/// </remarks>
public sealed partial class TerminalView : Control
{
    /// <summary>Terminal font; null = the <c>CarbonFontMono</c> resource (IBM Plex Mono) or a platform monospace font.</summary>
    public static readonly StyledProperty<FontFamily?> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalView, FontFamily?>(nameof(FontFamily));

    /// <summary>Font size in DIPs (default 14).</summary>
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalView, double>(nameof(FontSize), 14, coerce: (_, v) => Math.Clamp(v, 6, 96));

    /// <summary>Font tried for characters the main font lacks (e.g. a CJK monospace font such as Sarasa Mono) before platform fallback.</summary>
    public static readonly StyledProperty<FontFamily?> FallbackFontFamilyProperty =
        AvaloniaProperty.Register<TerminalView, FontFamily?>(nameof(FallbackFontFamily));

    /// <summary>Colours; null = derived from the Carbon tokens of the current theme variant.</summary>
    public static readonly StyledProperty<TerminalTheme?> ColorThemeProperty =
        AvaloniaProperty.Register<TerminalView, TerminalTheme?>(nameof(ColorTheme));

    /// <summary>macOS: Option acts as Meta (sends ESC-prefixed keys) instead of composing characters (default true).</summary>
    public static readonly StyledProperty<bool> MacOptionIsMetaProperty =
        AvaloniaProperty.Register<TerminalView, bool>(nameof(MacOptionIsMeta), true);

    /// <summary>Default cursor shape; applications may change it with DECSCUSR.</summary>
    public static readonly StyledProperty<TerminalCursorStyle> CursorStyleProperty =
        AvaloniaProperty.Register<TerminalView, TerminalCursorStyle>(nameof(CursorStyle));

    /// <summary>Default cursor blink; applications may change it with DECSCUSR.</summary>
    public static readonly StyledProperty<bool> CursorBlinkProperty =
        AvaloniaProperty.Register<TerminalView, bool>(nameof(CursorBlink));

    /// <summary>Lines kept above the screen (default 10000).</summary>
    public static readonly StyledProperty<int> ScrollbackLinesProperty =
        AvaloniaProperty.Register<TerminalView, int>(nameof(ScrollbackLines), 10000, coerce: (_, v) => Math.Clamp(v, 0, 1_000_000));

    private static readonly FontFamily DefaultMono = new("Menlo, Consolas, DejaVu Sans Mono, Noto Sans Mono, monospace");

    private readonly ConcurrentQueue<byte[]> _pending = new();
    private readonly Queue<byte[]> _outQueue = new();
    private readonly List<RowVisual> _rows = [];
    private readonly CursorOverlay _overlay;
    private readonly TerminalImeClient _ime;
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _syncTimer;
    private long _syncStarted;
    private readonly Action _pumpAction;
    private ITerminalBackend? _backend;
    private Action<ReadOnlyMemory<byte>>? _dataHandler;
    private Action<TerminalExit>? _exitHandler;
    private long _pendingBytes;
    private long _droppedBytes;
    private long _lastAccessibilityNotify;
    private TopLevel? _topLevel;
    private int _pumpScheduled;
    private bool _writing;
    private bool _inputClosed;
    private long _lastContentVersion = -1;
    private int _epoch;
    private TerminalCursorStyle _cursorStyle;
    private bool _cursorBlink;
    private bool _blinkOn = true;
    private string? _lastAccessibleText;
    private TerminalViewAutomationPeer? _peer;

    /// <summary>Creates a view with an 80×24 engine; the grid follows the control size once laid out.</summary>
    public TerminalView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);

        Engine = new XTerm.Terminal(new TerminalOptions
        {
            Cols = 80,
            Rows = 24,
            Scrollback = ScrollbackLines,
            TermName = "xterm-256color",
            // OSC 52 and image protocols stay off until the app can authorise / render them (design §4.3)
            ClipboardReadEnabled = false,
            ClipboardWriteEnabled = false,
            SixelEnabled = false,
            KittyGraphicsEnabled = false,
            ITerm2ImagesEnabled = false,
            KittyKeyboardEnabled = false,
            AllowPasteControls = false,
            DrawBoldTextInBrightColors = true,
        });
        Engine.DataReceived += (_, e) => SendText(e.Data);
        Engine.TitleChanged += (_, e) =>
        {
            Title = e.Title ?? string.Empty;
            TitleChanged?.Invoke(this, Title);
        };
        Engine.DirectoryChanged += (_, e) =>
        {
            WorkingDirectory = e.Directory;
            WorkingDirectoryChanged?.Invoke(this, e.Directory);
        };
        Engine.BellRang += (_, _) => Bell?.Invoke(this, EventArgs.Empty);
        Engine.CursorStyleChanged += (_, e) =>
        {
            _cursorStyle = e.Style switch
            {
                XTerm.Common.CursorStyle.Bar => TerminalCursorStyle.Bar,
                XTerm.Common.CursorStyle.Underline => TerminalCursorStyle.Underline,
                _ => TerminalCursorStyle.Block,
            };
            _cursorBlink = e.Blink;
            RestartBlink();
        };
        Engine.Colors.ColorChanged += (_, _) => RebuildPalette(applyTheme: false);
        Engine.BufferChanged += (_, _) =>
        {
            ClearSelection();
            ClearSearch();
        };
        Engine.WindowInfoRequested += OnWindowInfoRequested;
        Engine.SynchronizedOutputChanged += (_, e) =>
        {
            if (e.Active)
            {
                _syncStarted = Stopwatch.GetTimestamp();
            }
            else
            {
                _syncTimer?.Stop(); // assigned below; the engine cannot raise this before the constructor returns
                Dispatcher.UIThread.Post(AfterContentChange, DispatcherPriority.Input);
            }
        };

        _cursorStyle = CursorStyle;
        _cursorBlink = CursorBlink;
        _pumpAction = () => Pump(timeBoxed: true);
        _ime = new TerminalImeClient(this);
        TextInputMethodClientRequested += (_, e) => e.Client = _ime;
        ActualThemeVariantChanged += (_, _) =>
        {
            if (ColorTheme is null)
            {
                RebuildPalette(applyTheme: true);
            }
        };
        _overlay = new CursorOverlay(this);
        VisualChildren.Add(_overlay);
        _blinkTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            _overlay.InvalidateVisual();
        };
        _syncTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(210) };
        _syncTimer.Tick += (_, _) =>
        {
            _syncTimer.Stop();
            AfterContentChange();
        };
        _searchRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _searchRefreshTimer.Tick += (_, _) =>
        {
            _searchRefreshTimer.Stop();
            RerunSearch(keepCurrent: true);
        };
    }

    /// <summary>The session ended (shell exit or connection lost). Input is no longer sent after this.</summary>
    public event EventHandler<TerminalExit>? SessionEnded;

    /// <summary>OSC 0/2 window title.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>OSC 7 current directory (path only, e.g. <c>/home/deploy/app</c>).</summary>
    public event EventHandler<string>? WorkingDirectoryChanged;

    /// <summary>BEL received.</summary>
    public event EventHandler? Bell;

    /// <summary>Ctrl+click (Cmd+click on macOS) on a URL or OSC 8 link. The app decides whether to open it.</summary>
    public event EventHandler<Uri>? LinkActivated;

    /// <summary>Before a paste is sent; lets the app confirm multi-line pastes, rewrite or cancel them.</summary>
    public event EventHandler<TerminalPasteEventArgs>? PasteRequested;

    /// <summary><see cref="Columns"/> / <see cref="Rows"/> changed.</summary>
    public event EventHandler? GridSizeChanged;

    /// <summary>The selection changed (made, extended or cleared).</summary>
    public event EventHandler? SelectionChanged;

    /// <inheritdoc cref="FontFamilyProperty"/>
    public FontFamily? FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <inheritdoc cref="FontSizeProperty"/>
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <inheritdoc cref="FallbackFontFamilyProperty"/>
    public FontFamily? FallbackFontFamily
    {
        get => GetValue(FallbackFontFamilyProperty);
        set => SetValue(FallbackFontFamilyProperty, value);
    }

    /// <inheritdoc cref="ColorThemeProperty"/>
    public TerminalTheme? ColorTheme
    {
        get => GetValue(ColorThemeProperty);
        set => SetValue(ColorThemeProperty, value);
    }

    /// <inheritdoc cref="MacOptionIsMetaProperty"/>
    public bool MacOptionIsMeta
    {
        get => GetValue(MacOptionIsMetaProperty);
        set => SetValue(MacOptionIsMetaProperty, value);
    }

    /// <inheritdoc cref="CursorStyleProperty"/>
    public TerminalCursorStyle CursorStyle
    {
        get => GetValue(CursorStyleProperty);
        set => SetValue(CursorStyleProperty, value);
    }

    /// <inheritdoc cref="CursorBlinkProperty"/>
    public bool CursorBlink
    {
        get => GetValue(CursorBlinkProperty);
        set => SetValue(CursorBlinkProperty, value);
    }

    /// <inheritdoc cref="ScrollbackLinesProperty"/>
    public int ScrollbackLines
    {
        get => GetValue(ScrollbackLinesProperty);
        set => SetValue(ScrollbackLinesProperty, value);
    }

    /// <summary>The VT engine (screen buffer, modes). Mutate it only on the UI thread.</summary>
    public XTerm.Terminal Engine { get; }

    /// <summary>Grid width in cells.</summary>
    public int Columns => Engine.Cols;

    /// <summary>Grid height in cells.</summary>
    public int Rows => Engine.Rows;

    /// <summary>Last OSC 0/2 title ("" until the remote sets one).</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Last OSC 7 directory, or null.</summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>The attached backend, or null.</summary>
    public ITerminalBackend? Backend => _backend;

    /// <summary>True after <see cref="SessionEnded"/> until the next <see cref="Attach"/>.</summary>
    public bool IsSessionEnded => _inputClosed && _backend is not null;

    /// <summary>Maximum bytes parsed per UI slice while draining a flood (default 4 MB).</summary>
    public int BytesPerFrameBudget { get; set; } = 4 * 1024 * 1024;

    /// <summary>Unparsed output kept before the oldest chunks are dropped (default 256 MB): the backend has no back-pressure.</summary>
    public long MaxPendingBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Output dropped because <see cref="MaxPendingBytes"/> was exceeded.</summary>
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    /// <summary>Cell size in DIPs.</summary>
    public Size CellSize => Fonts is { } f ? new Size(f.CellWidth, f.CellHeight) : default;

    /// <summary>True while the viewport shows scrollback instead of the live screen.</summary>
    public bool IsScrolledBack => !Engine.Buffer.IsAtBottom;

    internal TerminalFonts? Fonts { get; private set; }
    internal RowBuilder? Builder { get; private set; }
    internal RenderStats Stats { get; } = new();
    internal string? Preedit => _ime.Preedit;
    internal int? PreeditCursor => _ime.PreeditCursor;
    internal TerminalImeClient ImeClient => _ime;
    internal TerminalCursorStyle EffectiveCursorStyle => _cursorStyle;
    internal bool CursorBlinkVisible => !_cursorBlink || _blinkOn || !IsFocused;
    internal IReadOnlyList<RowVisual> RowVisuals => _rows;
    internal TerminalTheme? ResolvedTheme => Builder?.Palette.Theme;

    internal FontFamily EffectiveFontFamily =>
        FontFamily ?? (this.TryFindResource("CarbonFontMono", out var v) && v is FontFamily ff ? ff : DefaultMono);

    // ------------------------------------------------------------------ backend

    /// <summary>
    /// Connects the view to a backend: output is rendered, keyboard/mouse/paste and engine answers are written
    /// to it, and grid size changes are forwarded as window-change requests. Replaces any previous backend.
    /// </summary>
    public void Attach(ITerminalBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        Detach();
        _backend = backend;
        _inputClosed = false;
        _dataHandler = OnBackendData;
        _exitHandler = exit => Dispatcher.UIThread.Post(() => OnBackendExited(backend, exit));
        backend.DataReceived += _dataHandler;
        backend.Exited += _exitHandler;
        backend.Resize(Engine.Cols, Engine.Rows);
    }

    /// <summary>Disconnects from the backend (does not dispose it). Queued output that was not parsed yet is discarded.</summary>
    public void Detach()
    {
        if (_backend is { } b)
        {
            if (_dataHandler is not null)
            {
                b.DataReceived -= _dataHandler;
            }

            if (_exitHandler is not null)
            {
                b.Exited -= _exitHandler;
            }
        }

        _backend = null;
        _dataHandler = null;
        _exitHandler = null;
        _outQueue.Clear();
        while (_pending.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _pendingBytes, 0);
    }

    /// <summary>Writes output directly into the terminal (UI thread), e.g. local status messages.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        Engine.Write(data);
        AfterContentChange();
    }

    /// <summary>Writes text directly into the terminal (UI thread). Use "\r\n" for new lines.</summary>
    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    /// <summary>Sends text to the backend as if typed (no paste bracketing).</summary>
    public void SendInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ScrollToBottomOnInput();
        SendText(text);
    }

    private void OnBackendData(ReadOnlyMemory<byte> data)
    {
        // background thread: copy (the memory is only valid during the callback), queue, schedule the pump
        if (data.IsEmpty)
        {
            return;
        }

        var chunk = data.ToArray();
        _pending.Enqueue(chunk);
        var total = Interlocked.Add(ref _pendingBytes, chunk.Length);
        while (total > MaxPendingBytes && _pending.TryDequeue(out var dropped))
        {
            total = Interlocked.Add(ref _pendingBytes, -dropped.Length);
            Interlocked.Add(ref _droppedBytes, dropped.Length);
        }

        if (Interlocked.Exchange(ref _pumpScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(_pumpAction, DispatcherPriority.Input);
        }
    }

    /// <summary>Parses queued output. Time-boxed slices keep input and rendering responsive during floods.</summary>
    internal void Pump(bool timeBoxed)
    {
        Volatile.Write(ref _pumpScheduled, 0);
        var start = Stopwatch.GetTimestamp();
        var budget = BytesPerFrameBudget;
        var any = false;
        while ((!timeBoxed || (budget > 0 && Stopwatch.GetElapsedTime(start).TotalMilliseconds < 12)) && _pending.TryDequeue(out var chunk))
        {
            Interlocked.Add(ref _pendingBytes, -chunk.Length);
            Engine.Write(chunk);
            budget -= chunk.Length;
            any = true;
        }

        if (any)
        {
            AfterContentChange();
        }

        if (!_pending.IsEmpty && Interlocked.Exchange(ref _pumpScheduled, 1) == 0)
        {
            // more to do: yield to rendering/input first (a flood then renders at a reduced frame rate)
            Dispatcher.UIThread.Post(_pumpAction, DispatcherPriority.Background);
        }
    }

    /// <summary>Test/diagnostic hook: parse everything queued right now.</summary>
    internal void ProcessPendingOutput() => Pump(timeBoxed: false);

    private void OnBackendExited(ITerminalBackend backend, TerminalExit exit)
    {
        if (!ReferenceEquals(backend, _backend) || _inputClosed)
        {
            return;
        }

        ProcessPendingOutput();
        _inputClosed = true;
        _outQueue.Clear();
        _overlay.InvalidateVisual();
        SessionEnded?.Invoke(this, exit);
    }

    private void SendText(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            SendBytes(Encoding.UTF8.GetBytes(text));
        }
    }

    private void SendBytes(byte[] data)
    {
        if (_backend is not { } backend || _inputClosed || data.Length == 0)
        {
            return;
        }

        _outQueue.Enqueue(data);
        if (!_writing)
        {
            _ = DrainWritesAsync(backend);
        }
    }

    private async Task DrainWritesAsync(ITerminalBackend backend)
    {
        _writing = true;
        try
        {
            // runs on the UI thread; a synchronously completing backend write never leaves it
            while (ReferenceEquals(backend, _backend) && _outQueue.TryDequeue(out var chunk))
            {
                var pending = backend.WriteAsync(chunk);
                if (!pending.IsCompletedSuccessfully)
                {
                    await pending;
                }
            }
        }
#pragma warning disable CA1031 // a broken transport is reported through ITerminalBackend.Exited; input is just dropped
        catch (Exception)
        {
            _outQueue.Clear();
        }
#pragma warning restore CA1031
        finally
        {
            _writing = false;
        }
    }

    // ------------------------------------------------------------------ public helpers

    /// <summary>
    /// Text of the live screen (not the scrolled-back viewport), right-trimmed, trailing blank lines removed,
    /// at most the last <paramref name="maxLines"/> lines. Used by the AI assistant and MCP <c>read_terminal</c>.
    /// </summary>
    public string GetVisibleText(int maxLines)
    {
        var buf = Engine.Buffer;
        var lines = new List<string>(Engine.Rows);
        for (var r = 0; r < Engine.Rows; r++)
        {
            var line = buf.GetLine(buf.YBase + r);
            lines.Add(line?.TranslateToString(true, 0, Engine.Cols) ?? string.Empty);
        }

        var end = lines.Count;
        while (end > 0 && lines[end - 1].Length == 0)
        {
            end--;
        }

        var start = Math.Max(0, end - Math.Max(0, maxLines));
        return string.Join('\n', lines.Skip(start).Take(end - start));
    }

    /// <summary>
    /// The last <paramref name="maxLines"/> logical lines of scrollback + live screen as plain text (UI thread). Soft-wrapped
    /// rows are joined into one line; trailing blank rows are dropped. Used by MCP / AI <c>read_terminal</c>.
    /// </summary>
    public string GetRecentText(int maxLines)
    {
        var buf = Engine.Buffer;
        var last = Math.Min(buf.Lines.Length - 1, buf.YBase + Engine.Rows - 1);
        while (last >= 0 && (buf.GetLine(last)?.TranslateToString(true, 0, Engine.Cols) ?? string.Empty).Length == 0)
        {
            last--;
        }

        var lines = new List<string>();
        var sb = new StringBuilder();
        for (var r = last; r >= 0 && lines.Count < Math.Max(0, maxLines); r--)
        {
            var line = buf.GetLine(r);
            // Wrapped rows keep their trailing spaces (they are part of the text), the final row is right-trimmed.
            var wrappedInto = r < last && buf.GetLine(r + 1) is { IsWrapped: true };
            sb.Insert(0, line?.TranslateToString(!wrappedInto, 0, Engine.Cols) ?? string.Empty);
            if (line is { IsWrapped: true } && r > 0)
            {
                continue;
            }

            lines.Add(sb.ToString());
            sb.Clear();
        }

        lines.Reverse();
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Types <paramref name="text"/> on behalf of a tool call that was already authorized: no <see cref="PasteRequested"/>
    /// confirmation. Multi-line text goes through the engine's paste path (bracketed when the application enabled mode 2004);
    /// without bracketed paste, line breaks would execute lines, so multi-line text is refused unless
    /// <paramref name="pressEnter"/> is set. Returns false when the session is closed or the text was refused.
    /// </summary>
    public bool TypeText(string text, bool pressEnter)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_inputClosed || _backend is null)
        {
            return false;
        }

        if (!pressEnter && !Engine.BracketedPasteMode && text.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            return false;
        }

        ScrollToBottomOnInput();
        if (text.Length > 0)
        {
            Engine.Paste(text);
        }

        if (pressEnter)
        {
            SendText("\r");
        }

        return true;
    }

    /// <summary>Clears the screen and scrollback, keeping the cursor line (like xterm.js <c>clear()</c>).</summary>
    public void Clear()
    {
        Engine.Clear();
        ClearSelection();
        ClearSearch();
        AfterContentChange();
    }

    /// <summary>Full reset (RIS): modes, colours, screen and scrollback.</summary>
    public void Reset()
    {
        Engine.Reset();
        _cursorStyle = CursorStyle;
        _cursorBlink = CursorBlink;
        Engine.SetCursorStyle(ToEngine(_cursorStyle), _cursorBlink);
        RebuildPalette(applyTheme: true);
        ClearSelection();
        ClearSearch();
        AfterContentChange();
    }

    /// <summary>Moves keyboard focus to the terminal.</summary>
    public void FocusTerminal() => Focus();

    /// <summary>Scrolls the viewport by <paramref name="lines"/> (negative = up into scrollback).</summary>
    public void ScrollLines(int lines)
    {
        Engine.ScrollLines(lines);
        RefreshRows();
        _overlay.InvalidateVisual();
        _ime.NotifyCursorMoved();
    }

    /// <summary>Returns the viewport to the live screen.</summary>
    public void ScrollToBottom()
    {
        if (!Engine.Buffer.IsAtBottom)
        {
            Engine.ScrollToBottom();
            RefreshRows();
            _overlay.InvalidateVisual();
        }
    }

    // ------------------------------------------------------------------ content → rows

    private void AfterContentChange()
    {
        var v = Engine.RefreshContentVersion(); // P0 pitfall: ContentVersion is cached until refreshed
        if (Engine.SynchronizedOutput && Stopwatch.GetElapsedTime(_syncStarted).TotalMilliseconds < 200)
        {
            // mode 2026: the application is mid-frame; show it when it ends (SynchronizedOutputChanged) or after 200 ms
            _syncTimer.Stop();
            _syncTimer.Start();
            return;
        }

        if (v != _lastContentVersion)
        {
            _lastContentVersion = v;
            RefreshRows();
            if (_searchActive)
            {
                _searchRefreshTimer.Stop();
                _searchRefreshTimer.Start();
            }

            if (_peer is not null && Stopwatch.GetElapsedTime(_lastAccessibilityNotify).TotalMilliseconds > 500)
            {
                // screen readers get at most two updates a second, never one per output chunk
                _lastAccessibilityNotify = Stopwatch.GetTimestamp();
                var text = GetVisibleText(Engine.Rows);
                _peer.NotifyTextChanged(_lastAccessibleText, text);
                _lastAccessibleText = text;
            }
        }

        _blinkOn = true;
        _overlay.InvalidateVisual();
        _ime.NotifyCursorMoved();
    }

    /// <summary>
    /// Maps viewport rows to row visuals. A line keeps its visual (and recorded drawing) while it is unchanged:
    /// the engine clears <c>BufferLine.Cache</c> on every mutation, so "Cache still points at my visual and the
    /// version matches" means the drawing is current. Only changed lines are rebuilt; scrolled lines just move.
    /// </summary>
    internal void RefreshRows()
    {
        if (Builder is null)
        {
            return;
        }

        var buf = Engine.Buffer;
        var rows = Engine.Rows;
        var trimmed = buf.TrimmedLines;
        foreach (var v in _rows)
        {
            v.InUse = false;
        }

        EnsureRowPool(rows);

        Span<int> assigned = rows <= 512 ? stackalloc int[rows] : new int[rows];
        assigned.Fill(-1);
        for (var r = 0; r < rows; r++)
        {
            var line = buf.GetLine(buf.YDisp + r);
            if (line?.Cache is RowVisual v && ReferenceEquals(v.Line, line) && !v.InUse && v.Epoch == _epoch && v.LineVersion == line.Version)
            {
                v.InUse = true;
                assigned[r] = v.PoolIndex;
            }
        }

        var free = 0;
        for (var r = 0; r < rows; r++)
        {
            var abs = buf.YDisp + r;
            var line = buf.GetLine(abs);
            RowVisual v;
            if (assigned[r] >= 0)
            {
                v = _rows[assigned[r]];
            }
            else
            {
                while (_rows[free].InUse)
                {
                    free++;
                }

                v = _rows[free];
                v.InUse = true;
                v.Line = line;
                v.LineVersion = line?.Version ?? 0;
                v.Epoch = _epoch;
                if (line is not null)
                {
                    line.Cache = v;
                }

                v.Deco = DecoFor(abs, abs + trimmed);
                v.AbsRow = abs;
                v.MarkDirty();
            }

            v.AbsRow = abs;
            var deco = DecoFor(abs, abs + trimmed);
            if (deco != v.Deco)
            {
                v.Deco = deco;
                v.MarkDirty();
            }

            v.Slot = r; // only a transform change when it moved: its recorded drawing is replayed, not rebuilt

            if (!v.IsVisible)
            {
                v.IsVisible = true;
            }
        }

        foreach (var v in _rows)
        {
            if (!v.InUse && v.Line is not null)
            {
                v.Line = null;
                v.Slot = -1;
                v.IsVisible = false;
                v.ReleaseContent();
            }
        }

    }

    private void EnsureRowPool(int rows)
    {
        while (_rows.Count < rows)
        {
            var v = new RowVisual(this) { IsVisible = false, PoolIndex = _rows.Count };
            _rows.Add(v);
            VisualChildren.Insert(VisualChildren.Count - 1, v); // keep the cursor overlay on top
        }

        while (_rows.Count > rows)
        {
            var extra = _rows[^1];
            _rows.RemoveAt(_rows.Count - 1);
            extra.Line = null; // a line's Cache may still point here: it must never match again
            extra.ReleaseContent();
            VisualChildren.Remove(extra);
        }
    }

    private RowDeco DecoFor(int absRow, long stableRow)
    {
        var (ss, se) = _selection.SpanOnRow(stableRow, Engine.Cols);
        int hs = -1, he = -1;
        if (_hoverLink is { } link)
        {
            foreach (var (r, s, e) in link.Segments)
            {
                if (r == absRow)
                {
                    hs = s;
                    he = e;
                }
            }
        }

        return new RowDeco(ss, se, SearchStampForRow(stableRow), hs, he);
    }

    // ------------------------------------------------------------------ resources, theme, layout

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged += OnScalingChanged;
        }

        RebuildFonts();
        RebuildPalette(applyTheme: true);
        RestartBlink();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _blinkTimer.Stop();
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    // device-pixel snapping (cell height, box drawing) depends on the scaling: moving to another monitor rebuilds
    private void OnScalingChanged(object? sender, EventArgs e) => RebuildFonts();

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty || change.Property == FallbackFontFamilyProperty)
        {
            RebuildFonts();
        }
        else if (change.Property == ColorThemeProperty)
        {
            RebuildPalette(applyTheme: true);
        }
        else if (change.Property == CursorStyleProperty || change.Property == CursorBlinkProperty)
        {
            _cursorStyle = CursorStyle;
            _cursorBlink = CursorBlink;
            Engine.SetCursorStyle(ToEngine(_cursorStyle), _cursorBlink);
            RestartBlink();
        }
        else if (change.Property == ScrollbackLinesProperty)
        {
            Engine.Buffer.SetScrollback(ScrollbackLines);
        }
    }

    private void RebuildFonts()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        Fonts = new TerminalFonts(EffectiveFontFamily, FallbackFontFamily, FontSize, scaling);
        Engine.Options.CellWidthPixels = Math.Max(1, (int)Math.Round(Fonts.CellWidth * scaling));
        Engine.Options.CellHeightPixels = Math.Max(1, (int)Math.Round(Fonts.CellHeight * scaling));
        RebuildBuilder();
        InvalidateMeasure();
        UpdateGrid(Bounds.Size);
    }

    private void RebuildPalette(bool applyTheme)
    {
        var theme = ColorTheme ?? ThemeFromResources();
        if (applyTheme)
        {
            Engine.Colors.ApplyTheme(theme.ToEngineTheme());
        }

        _palette = new RenderPalette(theme, Engine.Colors);
        RebuildBuilder();
        InvalidateVisual();
    }

    private RenderPalette? _palette;

    private void RebuildBuilder()
    {
        if (Fonts is null)
        {
            return;
        }

        _palette ??= new RenderPalette(ColorTheme ?? ThemeFromResources(), Engine.Colors);
        Builder = new RowBuilder(Fonts, _palette, Engine.Options.DrawBoldTextInBrightColors);
        _epoch++;
        RefreshRows();
        _overlay.InvalidateVisual();
    }

    private TerminalTheme ThemeFromResources()
    {
        var variant = ActualThemeVariant;
        var dark = IsDark(variant);
        var t = dark ? TerminalTheme.CarbonDark : TerminalTheme.CarbonLight;
        if (TryColor("CarbonBackgroundColor", variant, out var bg))
        {
            t.Background = bg;
            t.CursorText = bg;
        }

        if (TryColor("CarbonTextPrimaryColor", variant, out var fg))
        {
            t.Foreground = fg;
            t.Cursor = fg;
        }

        if (TryColor("CarbonHighlightColor", variant, out var hl))
        {
            t.SelectionBackground = hl;
        }

        if (TryColor("CarbonLinkPrimaryColor", variant, out var link))
        {
            t.Link = link;
        }

        if (TryColor("CarbonInteractiveColor", variant, out var interactive))
        {
            t.PreeditUnderline = interactive;
        }

        return t;
    }

    private bool TryColor(string key, ThemeVariant variant, out Color color)
    {
        if (this.TryFindResource(key, variant, out var v) && v is Color c)
        {
            color = c;
            return true;
        }

        color = default;
        return false;
    }

    private static bool IsDark(ThemeVariant? v)
    {
        for (var cur = v; cur is not null; cur = cur.InheritVariant)
        {
            if (cur == ThemeVariant.Dark)
            {
                return true;
            }

            if (cur == ThemeVariant.Light)
            {
                return false;
            }
        }

        return true; // terminals default to dark
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Fonts is null)
        {
            RebuildFonts();
        }

        var f = Fonts!;
        var w = double.IsInfinity(availableSize.Width) ? 80 * f.CellWidth : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 24 * f.CellHeight : availableSize.Height;
        var rowSize = new Size(Engine.Cols * f.CellWidth, f.CellHeight);
        foreach (var r in _rows)
        {
            r.Measure(rowSize);
        }

        _overlay.Measure(new Size(w, h));
        return new Size(w, h);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Fonts is { } f)
        {
            // every row is arranged at the top; its RenderTransform puts it on its slot (see RowVisual.Slot)
            var rowRect = new Rect(0, 0, Math.Ceiling(Engine.Cols * f.CellWidth) + 1, f.CellHeight);
            foreach (var r in _rows)
            {
                r.Arrange(rowRect);
            }
        }

        _overlay.Arrange(new Rect(finalSize));
        return finalSize;
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateGrid(e.NewSize);
    }

    private void UpdateGrid(Size size)
    {
        if (Fonts is not { } f || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var cols = Math.Max(2, (int)Math.Floor((size.Width + 0.001) / f.CellWidth));
        var rows = Math.Max(1, (int)Math.Floor((size.Height + 0.001) / f.CellHeight));
        if (cols == Engine.Cols && rows == Engine.Rows)
        {
            return;
        }

        Engine.Resize(cols, rows);
        _epoch++;
        if (_backend is { } b && !_inputClosed)
        {
            b.Resize(cols, rows);
        }

        GridSizeChanged?.Invoke(this, EventArgs.Empty);
        InvalidateMeasure();
        AfterContentChange();
        RefreshRows();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (Builder is { } b)
        {
            context.FillRectangle(b.Palette.Brush(b.Palette.Background), new Rect(Bounds.Size));
        }
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new TerminalViewAutomationPeer(this);

    // ------------------------------------------------------------------ cursor / IME plumbing

    internal Rect GetImeCursorRect()
    {
        if (Fonts is not { } f)
        {
            return default;
        }

        var b = Engine.Buffer;
        var row = Math.Clamp(b.YBase + b.Y - b.YDisp, 0, Engine.Rows - 1);
        return new Rect(f.ColumnX(Math.Min(b.X, Engine.Cols - 1)), row * f.CellHeight, f.CellWidth, f.CellHeight);
    }

    internal void OnPreeditChanged() => _overlay.InvalidateVisual();

    private void RestartBlink()
    {
        _blinkOn = true;
        _blinkTimer.Stop();
        if (_cursorBlink && IsFocused && VisualRoot is not null)
        {
            _blinkTimer.Start();
        }

        _overlay.InvalidateVisual();
    }

    private static XTerm.Common.CursorStyle ToEngine(TerminalCursorStyle s) => s switch
    {
        TerminalCursorStyle.Bar => XTerm.Common.CursorStyle.Bar,
        TerminalCursorStyle.Underline => XTerm.Common.CursorStyle.Underline,
        _ => XTerm.Common.CursorStyle.Block,
    };

    private void OnWindowInfoRequested(object? sender, XTerm.Events.TerminalEvents.WindowInfoRequestedEventArgs e)
    {
        // report the grid, not the control (images derive the cell size from these)
        switch (e.Request)
        {
            case XTerm.Common.WindowInfoRequest.SizePixels:
                e.WidthPixels = Engine.Cols * Engine.Options.CellWidthPixels;
                e.HeightPixels = Engine.Rows * Engine.Options.CellHeightPixels;
                e.Handled = true;
                break;
            case XTerm.Common.WindowInfoRequest.CellSizePixels:
                e.CellWidth = Engine.Options.CellWidthPixels;
                e.CellHeight = Engine.Options.CellHeightPixels;
                e.Handled = true;
                break;
        }
    }
}
