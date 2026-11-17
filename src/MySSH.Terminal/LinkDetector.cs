using System.Text;
using System.Text.RegularExpressions;
using XTerm.Buffer;

namespace MySSH.Terminal;

/// <summary>A link found under the pointer: its target and the cells it covers (absolute rows, inclusive columns).</summary>
internal sealed record LinkHit(Uri Uri, IReadOnlyList<(int AbsRow, int Start, int End)> Segments, bool IsHyperlink);

/// <summary>OSC 8 hyperlinks first, then plain-text URLs across soft-wrapped lines.</summary>
internal static partial class LinkDetector
{
    private const string TrailingPunctuation = ".,;:!?'\"";

    /// <summary>Rows of a soft-wrapped line searched above and below the pointer (a URL rarely spans more).</summary>
    internal const int MaxWrappedRows = 32;

    [GeneratedRegex(@"(?:(?:https?|ftp|sftp|ssh|file)://|mailto:|www\.)[^\s<>""'`\u0000-\u001f]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    public static LinkHit? Find(TerminalBuffer buf, int absRow, int col, int cols)
    {
        var line = buf.GetLine(absRow);
        if (line is null || col < 0 || col >= line.Length)
        {
            return null;
        }

        if (line.HasLinks && line.TryGetLinkAt(col, out var link) && Uri.TryCreate(link.Url, UriKind.Absolute, out var target))
        {
            return new LinkHit(target, [(absRow, link.Column, Math.Min(cols, link.Column + link.Cols) - 1)], true);
        }

        // logical line: walk soft wraps both ways, at most MaxWrappedRows around the pointer (a minified file printed as
        // one line spans thousands of rows, and this runs on every pointer move while the link modifier is held)
        var first = absRow;
        while (first > 0 && absRow - first < MaxWrappedRows && buf.GetLine(first) is { IsWrapped: true })
        {
            first--;
        }

        var last = absRow;
        while (last + 1 < buf.Lines.Length && buf.GetLine(last + 1) is { IsWrapped: true } && last - absRow < MaxWrappedRows)
        {
            last++;
        }

        var sb = new StringBuilder();
        var map = new List<(int Row, int Col)>();
        var hitIndex = -1;
        for (var r = first; r <= last; r++)
        {
            var l = buf.GetLine(r);
            if (l is null)
            {
                continue;
            }

            var n = Math.Min(cols, l.Length);
            for (var c = 0; c < n; c++)
            {
                var cell = l[c];
                if (cell.Width == 0)
                {
                    if (r == absRow && c == col)
                    {
                        hitIndex = sb.Length - 1;
                    }

                    continue;
                }

                if (r == absRow && c == col)
                {
                    hitIndex = sb.Length;
                }

                var content = cell.CodePoint == 0 ? " " : cell.Content;
                foreach (var ch in content)
                {
                    sb.Append(ch);
                    map.Add((r, c));
                }
            }
        }

        if (hitIndex < 0)
        {
            return null;
        }

        var text = sb.ToString();
        foreach (Match m in UrlRegex().Matches(text))
        {
            var value = Trim(m.Value);
            var end = m.Index + value.Length - 1;
            if (hitIndex < m.Index || hitIndex > end)
            {
                continue;
            }

            var uriText = value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + value : value;
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var segments = new List<(int, int, int)>();
            var (sr, sc) = map[m.Index];
            var (er, ec) = map[end];
            for (var r = sr; r <= er; r++)
            {
                var start = r == sr ? sc : 0;
                var stop = r == er ? ec + (buf.GetLine(er)?.GetWidth(ec) == 2 ? 1 : 0) : cols - 1;
                segments.Add((r, start, stop));
            }

            return new LinkHit(uri, segments, false);
        }

        return null;
    }

    private static string Trim(string url)
    {
        var s = url;
        while (s.Length > 0)
        {
            var c = s[^1];
            if (TrailingPunctuation.Contains(c, StringComparison.Ordinal))
            {
                s = s[..^1];
            }
            else if ((c == ')' && Count(s, '(') < Count(s, ')')) || (c == ']' && Count(s, '[') < Count(s, ']')))
            {
                s = s[..^1];
            }
            else
            {
                break;
            }
        }

        return s;

        static int Count(string s, char c)
        {
            var n = 0;
            foreach (var x in s)
            {
                if (x == c)
                {
                    n++;
                }
            }

            return n;
        }
    }
}
