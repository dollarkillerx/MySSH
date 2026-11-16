using System.Text;
using XTerm.Buffer;

namespace MySSH.Terminal;

internal enum SelectionKind { Character, Word, Line, Block }

/// <summary>
/// Selection in "stable" row coordinates (absolute buffer row + lines trimmed from the scrollback so far),
/// so a selection stays on its text while output scrolls and old lines are evicted.
/// </summary>
internal sealed class TerminalSelection
{
    private const string WordDelimiters = " \t()[]{}<>'\"`,;|│";

    public (long Row, int Col)? Anchor { get; private set; }
    public (long Row, int Col)? Head { get; private set; }
    public SelectionKind Kind { get; private set; }

    /// <summary>The anchor word/line range for Word/Line drags.</summary>
    private (long Row, int Col) _anchorStart, _anchorEnd;

    public bool HasSelection => Anchor is not null && Head is not null;

    public void Clear()
    {
        Anchor = Head = null;
    }

    public void Start(TerminalBuffer buf, long trimmed, int absRow, int col, SelectionKind kind, int cols)
    {
        Kind = kind;
        var row = absRow + trimmed;
        switch (kind)
        {
            case SelectionKind.Word:
                var (s, e) = WordAt(buf, absRow, col, cols);
                _anchorStart = (row, s);
                _anchorEnd = (row, e);
                break;
            case SelectionKind.Line:
                var (first, last) = LogicalLine(buf, absRow);
                _anchorStart = (first + trimmed, 0);
                _anchorEnd = (last + trimmed, cols - 1);
                break;
            default:
                _anchorStart = _anchorEnd = (row, col);
                break;
        }

        Anchor = _anchorStart;
        Head = _anchorEnd;
    }

    public void Extend(TerminalBuffer buf, long trimmed, int absRow, int col, int cols)
    {
        if (Anchor is null)
        {
            return;
        }

        var row = absRow + trimmed;
        switch (Kind)
        {
            case SelectionKind.Word:
                {
                    var (s, e) = WordAt(buf, absRow, col, cols);
                    if (Compare((row, col), _anchorStart) < 0)
                    {
                        Anchor = _anchorEnd;
                        Head = (row, s);
                    }
                    else
                    {
                        Anchor = _anchorStart;
                        Head = (row, e);
                    }

                    break;
                }

            case SelectionKind.Line:
                {
                    var (first, last) = LogicalLine(buf, absRow);
                    if (row < _anchorStart.Row)
                    {
                        Anchor = _anchorEnd;
                        Head = (first + trimmed, 0);
                    }
                    else
                    {
                        Anchor = _anchorStart;
                        Head = (last + trimmed, cols - 1);
                    }

                    break;
                }

            default:
                Head = (row, col);
                break;
        }
    }

    public void SelectAll(TerminalBuffer buf, long trimmed, int cols)
    {
        Kind = SelectionKind.Character;
        Anchor = (trimmed, 0);
        Head = (buf.Lines.Length - 1 + trimmed, cols - 1);
    }

    /// <summary>Selected column span on a row (inclusive), or (-1,-1).</summary>
    public (int Start, int End) SpanOnRow(long stableRow, int cols)
    {
        if (Anchor is not { } a || Head is not { } h)
        {
            return (-1, -1);
        }

        if (Kind == SelectionKind.Block)
        {
            var top = Math.Min(a.Row, h.Row);
            var bottom = Math.Max(a.Row, h.Row);
            if (stableRow < top || stableRow > bottom)
            {
                return (-1, -1);
            }

            return (Math.Min(a.Col, h.Col), Math.Max(a.Col, h.Col));
        }

        var (s, e) = Compare(a, h) <= 0 ? (a, h) : (h, a);
        if (stableRow < s.Row || stableRow > e.Row)
        {
            return (-1, -1);
        }

        var from = stableRow == s.Row ? s.Col : 0;
        var to = stableRow == e.Row ? e.Col : cols - 1;
        return from > to ? (-1, -1) : (from, to);
    }

    public string? GetText(TerminalBuffer buf, long trimmed, int cols)
    {
        if (Anchor is not { } a || Head is not { } h)
        {
            return null;
        }

        var top = Math.Min(a.Row, h.Row) - trimmed;
        var bottom = Math.Max(a.Row, h.Row) - trimmed;
        top = Math.Max(0, top);
        bottom = Math.Min(buf.Lines.Length - 1, bottom);
        if (top > bottom)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (var r = top; r <= bottom; r++)
        {
            var (from, to) = SpanOnRow(r + trimmed, cols);
            if (from < 0)
            {
                continue;
            }

            var line = buf.GetLine((int)r);
            if (line is null)
            {
                continue;
            }

            // a wide character is selected as a whole when either half is
            if (from > 0 && from < line.Length && line.GetWidth(from) == 0)
            {
                from--;
            }

            var toExclusive = Math.Min(line.Length, to + 1);
            var reachesEnd = toExclusive >= Math.Min(cols, line.Length);
            var text = line.TranslateToString(reachesEnd, from, toExclusive);
            sb.Append(text);
            if (r != bottom)
            {
                var next = buf.GetLine((int)r + 1);
                var wrapped = Kind != SelectionKind.Block && next is not null && next.IsWrapped && reachesEnd;
                if (!wrapped)
                {
                    sb.Append('\n');
                }
            }
        }

        // blank rows below the content (e.g. Select All on a fresh screen) are not part of the text
        var end = sb.Length;
        while (end > 0 && sb[end - 1] == '\n')
        {
            end--;
        }

        return sb.ToString(0, end);
    }

    private static (int Start, int End) WordAt(TerminalBuffer buf, int absRow, int col, int cols)
    {
        var line = buf.GetLine(absRow);
        if (line is null)
        {
            return (col, col);
        }

        var n = Math.Min(cols, line.Length);
        bool IsWord(int c)
        {
            if (c < 0 || c >= n)
            {
                return false;
            }

            if (line.GetWidth(c) == 0)
            {
                return true; // spacer of a wide char belongs to its word
            }

            var cp = line.GetCodePoint(c);
            return cp > 32 && (cp > 0xFFFF || WordDelimiters.IndexOf((char)cp) < 0);
        }

        if (!IsWord(col))
        {
            return (col, col);
        }

        int s = col, e = col;
        while (IsWord(s - 1))
        {
            s--;
        }

        while (IsWord(e + 1))
        {
            e++;
        }

        return (s, e);
    }

    private static (int First, int Last) LogicalLine(TerminalBuffer buf, int absRow)
    {
        var first = absRow;
        while (first > 0 && buf.GetLine(first) is { IsWrapped: true })
        {
            first--;
        }

        var last = absRow;
        while (last + 1 < buf.Lines.Length && buf.GetLine(last + 1) is { IsWrapped: true })
        {
            last++;
        }

        return (first, last);
    }

    private static int Compare((long Row, int Col) x, (long Row, int Col) y) =>
        x.Row != y.Row ? x.Row.CompareTo(y.Row) : x.Col.CompareTo(y.Col);
}
