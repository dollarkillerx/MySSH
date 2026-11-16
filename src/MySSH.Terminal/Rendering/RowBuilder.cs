using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using XTerm.Buffer;
using XTerm.Common;

namespace MySSH.Terminal.Rendering;

/// <summary>Per-row decoration state that is not part of the cell data; a change re-renders the row.</summary>
internal readonly record struct RowDeco(int SelStart, int SelEnd, int SearchStamp, int HoverStart, int HoverEnd)
{
    public static RowDeco None => new(-1, -1, 0, -1, -1);
}

/// <summary>A search hit on one row (columns are inclusive start, exclusive end).</summary>
internal readonly record struct HitSpan(int Start, int End, bool Current);

/// <summary>
/// Turns one <see cref="BufferLine"/> into a <see cref="RowContent"/>: merged background runs, glyphs batched by
/// (colour, face) into a handful of <see cref="GlyphRun"/>s with exact cell positions (no text shaping on the hot
/// path), procedural box drawing and decorations.
/// </summary>
internal sealed class RowBuilder
{
    private const int FlagBold = 1, FlagItalic = 2, FlagInvisible = 4, FlagStrike = 8, FlagOverline = 16;
    private static readonly string Placeholder = new(' ', 4096);

    private readonly List<GlyphBatch> _batches = [];
    private readonly Stack<GlyphBatch> _pool = new();

    public RowBuilder(TerminalFonts fonts, RenderPalette palette, bool boldIsBright)
    {
        Fonts = fonts;
        Palette = palette;
        BoldIsBright = boldIsBright;
    }

    public TerminalFonts Fonts { get; }
    public RenderPalette Palette { get; }
    public bool BoldIsBright { get; }

    public void Build(BufferLine line, int cols, in RowDeco deco, ReadOnlySpan<HitSpan> hits, RowContent o)
    {
        o.Clear();
        var f = Fonts;
        var p = Palette;
        var cw = f.CellWidth;
        var ch = f.CellHeight;
        var n = Math.Min(cols, line.Length);

        var bgStart = 0;
        uint bgRun = 0;
        var bgOn = false;

        // attribute resolution is memoised across cells with identical attributes
        int lastFg = int.MinValue, lastBg = int.MinValue, lastExt = int.MinValue;
        uint aFg = 0, aBg = 0, ulColor = 0;
        bool aHasBg = false, hasUlColor = false;
        int flags = 0, style = 0;
        var ulStyle = UnderlineStyle.None;

        GlyphBatch? lastBatch = null;

        for (var c = 0; c < n; c++)
        {
            var cell = line[c];
            var a = cell.Attributes;
            if (a.Fg != lastFg || a.Bg != lastBg || a.Extended != lastExt)
            {
                lastFg = a.Fg;
                lastBg = a.Bg;
                lastExt = a.Extended;
                Resolve(a, out aFg, out aBg, out aHasBg, out flags, out ulStyle, out hasUlColor, out ulColor);
                style = TerminalFonts.StyleIndex((flags & FlagBold) != 0, (flags & FlagItalic) != 0);
            }

            uint fg = aFg, bg = aBg;
            var hasBg = aHasBg;
            if (c >= deco.SelStart && c <= deco.SelEnd)
            {
                bg = p.SelectionBg;
                hasBg = true;
                if (p.SelectionFg is { } sf)
                {
                    fg = sf;
                }
            }

            foreach (var h in hits)
            {
                if (c >= h.Start && c < h.End)
                {
                    bg = h.Current ? p.CurrentBg : p.MatchBg;
                    fg = h.Current ? p.CurrentFg : p.MatchFg;
                    hasBg = true;
                }
            }

            if (hasBg && bg == p.Background)
            {
                hasBg = false;
            }

            if (hasBg != bgOn || (hasBg && bg != bgRun))
            {
                if (bgOn)
                {
                    FlushBg(o, bgStart, c, bgRun, ch);
                }

                bgOn = hasBg;
                bgRun = bg;
                bgStart = c;
            }

            var width = cell.Width;
            if (width <= 0)
            {
                continue;
            }

            fg = p.Contrast(fg, hasBg ? bg : p.Background);
            var cp = cell.CodePoint;
            var x0 = c * cw;
            if (cp > 32 && (flags & FlagInvisible) == 0)
            {
                var box = width * cw;
                if (cell.ClusterId == 0)
                {
                    if (BoxDrawing.Handles(cp))
                    {
                        BoxDrawing.Draw(o, cp, f.ColumnX(c), f.ColumnX(c + width), 0, ch, p.Brush(fg), f);
                    }
                    else
                    {
                        var g = f.Get(cp, style);
                        if (NeedsScaling(g.Face, g.InkWidth, box))
                        {
                            DrawScaled(o, g.Face, [new GlyphInfo(g.Glyph, 0, g.Advance, default)], g.InkLeft, g.InkWidth, x0, box, fg);
                        }
                        else
                        {
                            lastBatch = Batch(lastBatch, fg, g.Face);
                            lastBatch.Add(g.Glyph, x0 + ((box - g.Advance) / 2), default, g.Advance);
                        }
                    }
                }
                else
                {
                    var cl = f.GetCluster(cell.Content, style);
                    if (NeedsScaling(cl.Face, cl.InkWidth, box))
                    {
                        DrawScaled(o, cl.Face, cl.Glyphs, cl.InkLeft, cl.InkWidth, x0, box, fg);
                    }
                    else
                    {
                        lastBatch = Batch(lastBatch, fg, cl.Face);
                        var x = x0 + ((box - cl.Advance) / 2);
                        foreach (var gi in cl.Glyphs)
                        {
                            lastBatch.Add(gi.GlyphIndex, x, gi.GlyphOffset, gi.GlyphAdvance);
                            x += gi.GlyphAdvance;
                        }
                    }
                }
            }

            if (ulStyle != UnderlineStyle.None || (flags & (FlagStrike | FlagOverline)) != 0)
            {
                var xa = f.ColumnX(c);
                var xb = f.ColumnX(c + width);
                var brush = p.Brush(fg);
                if (ulStyle != UnderlineStyle.None)
                {
                    Underline(o, ulStyle, xa, xb, hasUlColor ? p.Brush(ulColor) : brush);
                }

                if ((flags & FlagStrike) != 0)
                {
                    o.Fg(brush, new Rect(xa, f.StrikeY, xb - xa, f.LineThickness));
                }

                if ((flags & FlagOverline) != 0)
                {
                    o.Fg(brush, new Rect(xa, 0, xb - xa, f.LineThickness));
                }
            }
        }

        if (bgOn)
        {
            FlushBg(o, bgStart, n, bgRun, ch);
        }

        // OSC 8 hyperlinks get a faint underline; a hovered link (Ctrl/Cmd held) a solid one
        if (line.HasLinks)
        {
            var faint = p.Brush((p.Link & 0x00FFFFFFu) | 0x90000000u);
            foreach (var link in line.Links)
            {
                var xa = f.ColumnX(link.Column);
                var xb = f.ColumnX(Math.Min(n, link.Column + link.Cols));
                o.Fg(faint, new Rect(xa, f.UnderlineY, xb - xa, f.LineThickness));
            }
        }

        if (deco.HoverStart >= 0)
        {
            var xa = f.ColumnX(deco.HoverStart);
            var xb = f.ColumnX(Math.Min(n, deco.HoverEnd + 1));
            o.Fg(p.Brush(p.Link), new Rect(xa, f.UnderlineY, xb - xa, f.LineThickness));
        }

        foreach (var b in _batches)
        {
            o.Run(p.Brush(b.Color), b.Build(f.FontSize, f.Baseline));
            b.Reset();
            _pool.Push(b);
        }

        _batches.Clear();
    }

    /// <summary>A glyph run for a single cell (block cursor text, preedit is drawn with FormattedText).</summary>
    public GlyphRun? BuildCell(in BufferCell cell, double x0, int style)
    {
        var cp = cell.CodePoint;
        if (cp <= 32 || cell.Width <= 0 || BoxDrawing.Handles(cp))
        {
            return null;
        }

        var box = cell.Width * Fonts.CellWidth;
        GlyphTypeface face;
        GlyphInfo[] infos;
        double advance;
        if (cell.ClusterId == 0)
        {
            var g = Fonts.Get(cp, style);
            face = g.Face;
            infos = [new GlyphInfo(g.Glyph, 0, g.Advance, default)];
            advance = g.Advance;
        }
        else
        {
            var cl = Fonts.GetCluster(cell.Content, style);
            face = cl.Face;
            infos = [.. cl.Glyphs];
            advance = cl.Advance;
        }

        return new GlyphRun(face, Fonts.FontSize, Placeholder.AsMemory(0, infos.Length), infos,
            new Point(x0 + Math.Max(0, (box - advance) / 2), Fonts.Baseline));
    }

    public void ResolveCell(in BufferCell cell, out uint fg, out uint bg, out int style)
    {
        Resolve(cell.Attributes, out fg, out bg, out var hasBg, out var flags, out _, out _, out _);
        if (!hasBg)
        {
            bg = Palette.Background;
        }

        style = TerminalFonts.StyleIndex((flags & FlagBold) != 0, (flags & FlagItalic) != 0);
    }

    private void Resolve(AttributeData a, out uint fg, out uint bg, out bool hasBg, out int flags, out UnderlineStyle ul, out bool hasUlColor, out uint ulColor)
    {
        var p = Palette;
        flags = 0;
        if (a.IsBold()) flags |= FlagBold;
        if (a.IsItalic()) flags |= FlagItalic;
        if (a.IsInvisible()) flags |= FlagInvisible;
        if (a.IsStrikethrough()) flags |= FlagStrike;
        if (a.IsOverline()) flags |= FlagOverline;

        var fgKind = a.GetFgColorKind();
        fg = fgKind switch
        {
            ColorMode.RGB => 0xFF000000u | (uint)a.GetFgColor(),
            ColorMode.Palette16 => p.Indexed(BoldIsBright && (flags & FlagBold) != 0 && (a.GetFgColor() & 0xff) < 8 ? (a.GetFgColor() & 0xff) + 8 : a.GetFgColor()),
            ColorMode.Palette256 => p.Indexed(a.GetFgColor()),
            _ => p.Foreground,
        };
        var bgKind = a.GetBgColorKind();
        hasBg = bgKind != ColorMode.Default;
        bg = bgKind switch
        {
            ColorMode.RGB => 0xFF000000u | (uint)a.GetBgColor(),
            ColorMode.Palette16 or ColorMode.Palette256 => p.Indexed(a.GetBgColor()),
            _ => p.Background,
        };

        if (a.IsInverse())
        {
            (fg, bg) = (bg, fg);
            hasBg = true;
        }

        if (a.IsDim())
        {
            fg = RenderPalette.Blend(fg, bg, 0.4);
        }

        ul = a.IsUnderline() ? a.GetUnderlineStyle() : UnderlineStyle.None;
        if (ul == UnderlineStyle.None && a.IsUnderline())
        {
            ul = UnderlineStyle.Single;
        }

        hasUlColor = a.TryGetUnderlineColor(out var uc, out var um);
        ulColor = hasUlColor ? (um == 1 ? 0xFF000000u | (uint)uc : p.Indexed(uc)) : 0;
    }

    private void FlushBg(RowContent o, int from, int to, uint color, double ch)
    {
        var xa = Fonts.ColumnX(from);
        var xb = Fonts.ColumnX(to);
        if (xb > xa)
        {
            o.Bg(Palette.Brush(color), new Rect(xa, 0, xb - xa, ch));
        }
    }

    private void Underline(RowContent o, UnderlineStyle style, double xa, double xb, IBrush brush)
    {
        var f = Fonts;
        var t = f.LineThickness;
        var y = f.UnderlineY;
        switch (style)
        {
            case UnderlineStyle.Double:
                o.Fg(brush, new Rect(xa, y, xb - xa, t));
                o.Fg(brush, new Rect(xa, Math.Min(f.CellHeight - t, y + (2 * t)), xb - xa, t));
                break;
            case UnderlineStyle.Dotted:
                for (var x = xa; x < xb; x += 2 * t)
                {
                    o.Bg(brush, new Rect(x, y, Math.Min(t, xb - x), t)); // Bg list: avoids merging the dots
                }

                break;
            case UnderlineStyle.Dashed:
                for (var x = xa; x < xb; x += 4 * t)
                {
                    o.Bg(brush, new Rect(x, y, Math.Min(3 * t, xb - x), t));
                }

                break;
            case UnderlineStyle.Curly:
                {
                    var geo = new StreamGeometry();
                    var amp = Math.Max(t, 1.5);
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(new Point(xa, y), false);
                        var step = (xb - xa) / 2;
                        for (var x = xa; x < xb - 0.01; x += step)
                        {
                            ctx.QuadraticBezierTo(new Point(x + (step / 4), y - amp), new Point(x + (step / 2), y));
                            ctx.QuadraticBezierTo(new Point(x + (3 * step / 4), y + amp), new Point(x + step, y));
                        }

                        ctx.EndFigure(false);
                    }

                    o.Geometry(null, new Pen(brush, t), geo);
                    break;
                }

            default:
                o.Fg(brush, new Rect(xa, y, xb - xa, t));
                break;
        }
    }

    /// <summary>
    /// Fallback glyphs wider than their cell box (colour emoji are ~1.25 em wide and their ink overhangs) are
    /// scaled down so they can never paint into the neighbouring cell; the primary face is never scaled.
    /// </summary>
    private bool NeedsScaling(GlyphTypeface face, double inkWidth, double box) =>
        inkWidth > box * (Fonts.IsPrimary(face) ? 1.15 : 1.0);

    private void DrawScaled(RowContent o, GlyphTypeface face, GlyphInfo[] glyphs, double inkLeft, double inkWidth, double x0, double box, uint fg)
    {
        var s = Math.Min(1, box * 0.92 / inkWidth);
        var run = new GlyphRun(face, Fonts.FontSize, Placeholder.AsMemory(0, glyphs.Length), glyphs, new Point(0, Fonts.Baseline / s));
        // scale about the pen origin and centre the ink in the cell box; the baseline stays on the primary font's
        var x = x0 + ((box - (inkWidth * s)) / 2) - (inkLeft * s);
        o.Run(Palette.Brush(fg), run, Matrix.CreateScale(s, s) * Matrix.CreateTranslation(x, 0));
    }

    private GlyphBatch Batch(GlyphBatch? last, uint color, GlyphTypeface face)
    {
        if (last is not null && last.Color == color && ReferenceEquals(last.Face, face))
        {
            return last;
        }

        foreach (var b in _batches)
        {
            if (b.Color == color && ReferenceEquals(b.Face, face))
            {
                return b;
            }
        }

        var nb = _pool.Count > 0 ? _pool.Pop() : new GlyphBatch();
        nb.Color = color;
        nb.Face = face;
        _batches.Add(nb);
        return nb;
    }

    /// <summary>Glyphs of one colour and face anywhere on the row, positioned by advances between them.</summary>
    private sealed class GlyphBatch
    {
        private readonly List<ushort> _glyphs = [];
        private readonly List<double> _x = [];
        private readonly List<Vector> _offsets = [];
        private double _lastAdvance;

        public uint Color { get; set; }
        public GlyphTypeface Face { get; set; } = null!;

        public void Add(ushort glyph, double x, Vector offset, double advance)
        {
            _glyphs.Add(glyph);
            _x.Add(x);
            _offsets.Add(offset);
            _lastAdvance = advance;
        }

        public GlyphRun Build(double fontSize, double baseline)
        {
            var n = _glyphs.Count;
            var infos = new GlyphInfo[n];
            for (var i = 0; i < n; i++)
            {
                var adv = i < n - 1 ? _x[i + 1] - _x[i] : _lastAdvance;
                infos[i] = new GlyphInfo(_glyphs[i], i, adv, _offsets[i]);
            }

            var chars = n <= Placeholder.Length ? Placeholder.AsMemory(0, n) : new string(' ', n).AsMemory();
            return new GlyphRun(Face, fontSize, chars, infos, new Point(_x[0], baseline));
        }

        public void Reset()
        {
            _glyphs.Clear();
            _x.Clear();
            _offsets.Clear();
            Face = null!;
        }
    }
}
