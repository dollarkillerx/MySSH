using System.Text;

namespace MySSH.Desktop.Views;

// A small Markdown subset for AI answers (no package: NativeAOT, and the model only uses a handful of constructs).
// Blocks: paragraphs, headings, bullet / numbered lists (nested by indentation), block quotes, rules, fenced code
// (an unterminated fence while streaming runs to the end), pipe tables (header + |---| separator). Inline: `code`, **bold** / __bold__, *italic*, [text](url).

public enum MdBlockKind { Paragraph, Heading, ListItem, Quote, Rule, Code, Table }

/// <param name="Level">Heading level (1–6) or list nesting depth (0-based).</param>
/// <param name="Marker">List marker as written ("•" for bullets, "3." for numbers).</param>
/// <param name="Language">Code fence info string.</param>
/// <param name="Rows">Table cells, header row first.</param>
public sealed record MdBlock(MdBlockKind Kind, string Text, int Level = 0, string? Marker = null, string? Language = null,
    IReadOnlyList<IReadOnlyList<string>>? Rows = null);

public enum MdSpanKind { Text, Bold, Italic, Code, Link }

public sealed record MdSpan(MdSpanKind Kind, string Text, string? Url = null);

public static class MarkdownParser
{
    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrEmpty(markdown)) return blocks;
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new MdBlock(MdBlockKind.Paragraph, paragraph.ToString()));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                var code = new StringBuilder();
                i++;
                for (; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal) && lines[i].Trim().Trim(fence[0]).Length == 0) break;
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                }
                blocks.Add(new MdBlock(MdBlockKind.Code, code.ToString(), Language: language.Length > 0 ? language : null));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            // Pipe table: a "| a | b |" row followed by a "|---|:---:|" separator.
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                FlushParagraph();
                var rows = new List<IReadOnlyList<string>> { SplitRow(trimmed) };
                i += 2;
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++) rows.Add(SplitRow(lines[i].Trim()));
                i--;
                var columns = rows[0].Count;
                blocks.Add(new MdBlock(MdBlockKind.Table, "", Rows: [.. rows.Select(r => (IReadOnlyList<string>)[.. Enumerable.Range(0, columns).Select(c => c < r.Count ? r[c] : "")])]));
                continue;
            }

            if (trimmed[0] == '#')
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();
                if (level <= 6 && trimmed.Length > level && trimmed[level] == ' ')
                {
                    FlushParagraph();
                    blocks.Add(new MdBlock(MdBlockKind.Heading, trimmed[(level + 1)..].Trim().TrimEnd('#').TrimEnd(), level));
                    continue;
                }
            }

            if (trimmed.Length >= 3 && (trimmed.All(c => c == '-' || c == ' ') || trimmed.All(c => c == '*' || c == ' ') || trimmed.All(c => c == '_' || c == ' '))
                && trimmed.Count(c => c != ' ') >= 3)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Rule, ""));
                continue;
            }

            if (ListItem(line) is { } item)
            {
                FlushParagraph();
                blocks.Add(item);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Quote, trimmed.TrimStart('>').TrimStart()));
                continue;
            }

            // A line continuing the previous list item (indented, no marker) joins it.
            if (paragraph.Length == 0 && blocks.Count > 0 && blocks[^1].Kind == MdBlockKind.ListItem && line.StartsWith("  ", StringComparison.Ordinal)
                && i > 0 && lines[i - 1].Trim().Length > 0)
            {
                blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + "\n" + trimmed };
                continue;
            }

            if (paragraph.Length > 0) paragraph.Append('\n');
            paragraph.Append(line.TrimEnd());
        }
        FlushParagraph();
        return blocks;
    }

    static bool IsTableSeparator(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith('|') || !t.Contains('-', StringComparison.Ordinal)) return false;
        var cells = SplitRow(t);
        return cells.Count > 0 && cells.All(c => c.Length > 0 && c.Trim(':').Length > 0 && c.Trim(':').All(ch => ch == '-'));
    }

    /// <summary>"| a | b \| c |" → ["a", "b | c"] (escaped pipes stay in the cell; inline code may contain pipes).</summary>
    static List<string> SplitRow(string row)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        var t = row.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith("\\|", StringComparison.Ordinal)) t = t[..^1];
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|') { cell.Append('|'); i++; continue; }
            if (c == '`') inCode = !inCode;
            if (c == '|' && !inCode) { cells.Add(cell.ToString().Trim()); cell.Clear(); continue; }
            cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    static MdBlock? ListItem(string line)
    {
        var indent = line.Length - line.TrimStart().Length;
        var t = line.TrimStart();
        if (t.Length >= 2 && t[0] is '-' or '*' or '+' && t[1] == ' ')
            return new MdBlock(MdBlockKind.ListItem, t[2..].TrimStart(), indent / 2, "•");
        var digits = t.TakeWhile(char.IsAsciiDigit).Count();
        if (digits is > 0 and < 4 && t.Length > digits + 1 && t[digits] is '.' or ')' && t[digits + 1] == ' ')
            return new MdBlock(MdBlockKind.ListItem, t[(digits + 2)..].TrimStart(), indent / 2, t[..(digits + 1)]);
        return null;
    }

    public static IReadOnlyList<MdSpan> ParseInline(string text)
    {
        var spans = new List<MdSpan>();
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length == 0) return;
            spans.Add(new MdSpan(MdSpanKind.Text, plain.ToString()));
            plain.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_[]()#+-.!>".Contains(text[i + 1], StringComparison.Ordinal))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }
            if (c == '`')
            {
                var ticks = 1;
                while (i + ticks < text.Length && text[i + ticks] == '`') ticks++;
                var fence = new string('`', ticks);
                var end = text.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                if (end > i + ticks)
                {
                    Flush();
                    spans.Add(new MdSpan(MdSpanKind.Code, text[(i + ticks)..end].Trim()));
                    i = end + ticks;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                var marker = new string(c, 2);
                var end = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i + 2 && !char.IsWhiteSpace(text[i + 2]))
                {
                    Flush();
                    spans.Add(new MdSpan(MdSpanKind.Bold, text[(i + 2)..end]));
                    i = end + 2;
                    continue;
                }
            }
            if (c == '*' && i + 1 < text.Length && text[i + 1] != ' ' && text[i + 1] != '*')
            {
                var end = text.IndexOf('*', i + 1);
                if (end > i + 1 && text[end - 1] != ' ')
                {
                    Flush();
                    spans.Add(new MdSpan(MdSpanKind.Italic, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }
            if (c == '[')
            {
                var close = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                var paren = close < 0 ? -1 : text.IndexOf(')', close + 2);
                if (close > i + 1 && paren > close + 2 && !text[(i + 1)..close].Contains('\n', StringComparison.Ordinal))
                {
                    Flush();
                    spans.Add(new MdSpan(MdSpanKind.Link, text[(i + 1)..close], text[(close + 2)..paren].Trim()));
                    i = paren + 1;
                    continue;
                }
            }
            plain.Append(c);
            i++;
        }
        Flush();
        return spans;
    }
}
