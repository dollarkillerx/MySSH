using System.Text;

namespace EngineEval;

/// <summary>Uniform view over a VT engine so the same test cases run against every candidate.</summary>
public interface IEngine
{
    string Name { get; }
    int Cols { get; }
    int Rows { get; }
    void Feed(ReadOnlySpan<byte> data);
    /// <summary>Visible row text; a wide glyph appears once, trailing blanks trimmed.</summary>
    string Row(int r);
    /// <summary>Display width the engine assigned to the cell (0 = trailing half of a wide glyph).</summary>
    int CellWidth(int r, int c);
    (int Row, int Col) Cursor { get; }
    string? Title { get; }
    int ScrollbackCount { get; }
    bool BracketedPaste { get; }
    bool AppCursorKeys { get; }
    bool MouseSgr { get; }
    bool MouseTracking { get; }
    string Responses { get; }
    string? FgDescription(int r, int c);
    string? HyperlinkAt(int r, int c);
    void Resize(int cols, int rows);
}

public sealed class XTermNetEngine : IEngine
{
    readonly XTerm.Terminal _t;
    readonly StringBuilder _resp = new();
    string? _title;
    public XTermNetEngine(int cols, int rows, int scrollback = 10000)
    {
        _t = new XTerm.Terminal(new XTerm.Options.TerminalOptions { Cols = cols, Rows = rows, Scrollback = scrollback });
        _t.DataReceived += (_, e) => _resp.Append(e.Data);
        _t.TitleChanged += (_, e) => _title = e.Title;
    }
    public string Name => "XTerm.NET 3.0.3";
    public int Cols => _t.Cols;
    public int Rows => _t.Rows;
    public void Feed(ReadOnlySpan<byte> data) => _t.Write(data);
    XTerm.Buffer.BufferLine Line(int r) => _t.Buffer.GetLine(_t.Buffer.YBase + r);
    public string Row(int r) => Line(r).TranslateToString(true, 0, _t.Cols);
    public int CellWidth(int r, int c) => Line(r).GetWidth(c);
    public (int, int) Cursor => (_t.Buffer.Y, _t.Buffer.X);
    public string? Title => _title ?? _t.Title;
    public int ScrollbackCount => _t.Buffer.YBase;
    public bool BracketedPaste => _t.BracketedPasteMode;
    public bool AppCursorKeys => _t.ApplicationCursorKeys;
    public bool MouseSgr => _t.MouseEncoding.ToString().Contains("Sgr", StringComparison.OrdinalIgnoreCase);
    public bool MouseTracking => !_t.MouseTrackingMode.ToString().Equals("None", StringComparison.OrdinalIgnoreCase);
    public string Responses => _resp.ToString();
    public string? FgDescription(int r, int c)
    {
        var a = Line(r)[c].Attributes;
        return $"{a.GetFgColorKind()}:{a.GetFgColor():X6}";
    }
    public string? HyperlinkAt(int r, int c) => Line(r).TryGetLinkAt(c, out var l) ? l.ToString() : null;
    public void Resize(int cols, int rows) => _t.Resize(cols, rows);
}

public sealed class TerminalEmulationEngine : IEngine
{
    readonly Terminal.Emulation.Terminal _t;
    readonly StringBuilder _resp = new();
    public TerminalEmulationEngine(int cols, int rows, int scrollback = 10000)
    {
        _t = new Terminal.Emulation.Terminal(cols, rows, scrollback);
        _t.Response += (_, e) => _resp.Append(Encoding.UTF8.GetString(ToBytes(e)));
    }
    static byte[] ToBytes(object e) => e switch
    {
        byte[] b => b,
        ReadOnlyMemory<byte> m => m.ToArray(),
        Memory<byte> m => m.ToArray(),
        string s => Encoding.UTF8.GetBytes(s),
        _ => Encoding.UTF8.GetBytes(e.ToString() ?? "")
    };
    public string Name => "Terminal.Emulation 0.4.5";
    public int Cols => _t.Columns;
    public int Rows => _t.Rows;
    public void Feed(ReadOnlySpan<byte> data) => _t.Write(data);
    Terminal.Emulation.TerminalLine Line(int r) => _t.GetRow(_t.TotalRows - _t.Rows + r);
    public string Row(int r)
    {
        var sb = new StringBuilder();
        foreach (var cell in Line(r).Cells)
        {
            if (cell.IsWideTrailing) continue;
            if (cell.IsBlank) { sb.Append(' '); continue; }
            cell.AppendGlyph(sb);
        }
        return sb.ToString().TrimEnd(' ', '\0');
    }
    public int CellWidth(int r, int c)
    {
        var cell = Line(r).Cells[c];
        return cell.IsWideTrailing ? 0 : cell.IsWideLeading ? 2 : 1;
    }
    public (int, int) Cursor => (_t.CursorRow, _t.CursorColumn);
    public string? Title => _t.Title;
    public int ScrollbackCount => _t.ScrollbackCount;
    public bool BracketedPaste => _t.Modes.BracketedPaste;
    public bool AppCursorKeys => _t.Modes.ApplicationCursorKeys;
    public bool MouseSgr => _t.Modes.MouseEncoding.ToString().Contains("Sgr", StringComparison.OrdinalIgnoreCase);
    public bool MouseTracking => !_t.Modes.Mouse.ToString().Equals("None", StringComparison.OrdinalIgnoreCase)
                                 && !_t.Modes.Mouse.ToString().Equals("Off", StringComparison.OrdinalIgnoreCase);
    public string Responses => _resp.ToString();
    public string? FgDescription(int r, int c)
    {
        var f = Line(r).Cells[c].Foreground;
        return $"{f.Kind}:{f.R:X2}{f.G:X2}{f.B:X2}/{f.Index}";
    }
    public string? HyperlinkAt(int r, int c)
    {
        var id = Line(r).Cells[c].Link;
        return id == 0 ? null : _t.GetHyperlink(id);
    }
    public void Resize(int cols, int rows) => _t.Resize(cols, rows, true);
}

public sealed class VirtualTerminalEngine : IEngine
{
    readonly VirtualTerminal.Buffer.TerminalScreenBuffer _buf;
    readonly VirtualTerminal.TerminalDecoder _d;
    readonly StringBuilder _resp = new();
    public VirtualTerminalEngine(int cols, int rows, int scrollback = 10000)
    {
        _buf = new VirtualTerminal.Buffer.TerminalScreenBuffer(cols, rows, scrollback);
        _d = new VirtualTerminal.TerminalDecoder(_buf, new VirtualTerminal.Options.TerminalOptions { ScrollbackMaxLines = scrollback });
        _d.AutoFlush = true;
        _d.SendOutput = b => _resp.Append(Encoding.UTF8.GetString(b.ToArray()));
        _cols = cols; _rows = rows;
    }
    int _cols, _rows;
    public string Name => "VirtualTerminal 1.11.0";
    public int Cols => _cols;
    public int Rows => _rows;
    public void Feed(ReadOnlySpan<byte> data) { _d.Write(data); _d.Flush(); }
    public string Row(int r)
    {
        var sb = new StringBuilder();
        foreach (var cell in _buf.GetRow(r))
        {
            var ch = cell.Character;
            if (ch.Value == 0) continue; // trailing half / empty
            sb.Append(ch.ToString());
        }
        return sb.ToString().TrimEnd(' ', '\0');
    }
    public int CellWidth(int r, int c)
    {
        var ch = _buf.GetCell(r, c).Character;
        return ch.Value == 0 ? 0 : VirtualTerminal.Model.Wcwidth.Of(ch);
    }
    public (int, int) Cursor => (_d.CursorPosition.Y, _d.CursorPosition.X);
    public string? Title => _d.Title;
    public int ScrollbackCount => _buf.GetScrollback().Count;
    public bool BracketedPaste => _d.State.Modes.BracketedPaste;
    public bool AppCursorKeys => _d.State.Modes.ApplicationCursorKeys;
    public bool MouseSgr => _d.State.Modes.SgrMouseEncoding;
    public bool MouseTracking => !_d.State.Modes.MouseTracking.ToString().Equals("None", StringComparison.OrdinalIgnoreCase)
                                 && !_d.State.Modes.MouseTracking.ToString().Equals("Off", StringComparison.OrdinalIgnoreCase);
    public string Responses => _resp.ToString();
    public string? FgDescription(int r, int c)
    {
        var s = _buf.GetCell(r, c).Style;
        return s.ToString();
    }
    public string? HyperlinkAt(int r, int c) => null;
    public void Resize(int cols, int rows) { _d.Resize((ushort)cols, (ushort)rows, true); _cols = cols; _rows = rows; }
}
