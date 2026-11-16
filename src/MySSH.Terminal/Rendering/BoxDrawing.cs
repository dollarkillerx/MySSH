using Avalonia;
using Avalonia.Media;

namespace MySSH.Terminal.Rendering;

/// <summary>
/// Procedural box drawing (U+2500–U+257F), block elements (U+2580–U+259F) and Powerline separators
/// (U+E0B0–U+E0BF). Strokes run exactly to the device-pixel-snapped cell edges, so lines join seamlessly
/// across cells and rows regardless of the font's own glyph metrics.
/// </summary>
internal static class BoxDrawing
{
    // Arm weights for U+2500–U+257F, 4 digits per char in the order Up, Right, Down, Left:
    // 0 = none, 1 = light, 2 = heavy, 3 = double. Dashed lines, arcs and diagonals are handled separately.
    private const string Arms =
        "0101" + "0202" + "1010" + "2020" + "0101" + "0202" + "1010" + "2020" + // 2500-2507
        "0101" + "0202" + "1010" + "2020" + "0110" + "0210" + "0120" + "0220" + // 2508-250F
        "0011" + "0012" + "0021" + "0022" + "1100" + "1200" + "2100" + "2200" + // 2510-2517
        "1001" + "1002" + "2001" + "2002" + "1110" + "1210" + "2110" + "1120" + // 2518-251F
        "2120" + "2210" + "1220" + "2220" + "1011" + "1012" + "2011" + "1021" + // 2520-2527
        "2021" + "2012" + "1022" + "2022" + "0111" + "0112" + "0211" + "0212" + // 2528-252F
        "0121" + "0122" + "0221" + "0222" + "1101" + "1102" + "1201" + "1202" + // 2530-2537
        "2101" + "2102" + "2201" + "2202" + "1111" + "1112" + "1211" + "1212" + // 2538-253F
        "2111" + "1121" + "2121" + "2112" + "2211" + "1122" + "1221" + "2212" + // 2540-2547
        "1222" + "2122" + "2221" + "2222" + "0101" + "0202" + "1010" + "2020" + // 2548-254F
        "0303" + "3030" + "0310" + "0130" + "0330" + "0013" + "0031" + "0033" + // 2550-2557
        "1300" + "3100" + "3300" + "1003" + "3001" + "3003" + "1310" + "3130" + // 2558-255F
        "3330" + "1013" + "3031" + "3033" + "0313" + "0131" + "0333" + "1303" + // 2560-2567
        "3101" + "3303" + "1313" + "3131" + "3333" + "0110" + "0011" + "1001" + // 2568-256F
        "1100" + "0000" + "0000" + "0000" + "0001" + "1000" + "0100" + "0010" + // 2570-2577
        "0002" + "2000" + "0200" + "0020" + "0201" + "1020" + "0102" + "2010";  // 2578-257F

    public static bool Handles(int cp) => cp is >= 0x2500 and <= 0x259F or >= 0xE0B0 and <= 0xE0BF;

    /// <summary>Draws <paramref name="cp"/> into the cell <c>[x0,x1) × [y0,y1)</c> (snapped DIPs, row-local).</summary>
    public static void Draw(RowContent ops, int cp, double x0, double x1, double y0, double y1, IBrush brush, TerminalFonts f)
    {
        if (cp >= 0xE0B0)
        {
            Powerline(ops, cp, x0, x1, y0, y1, brush, f);
        }
        else if (cp >= 0x2580)
        {
            Block(ops, cp, x0, x1, y0, y1, brush, f);
        }
        else
        {
            Line(ops, cp, x0, x1, y0, y1, brush, f);
        }
    }

    /// <summary>
    /// Adds a stroke rect with every edge on a device pixel: overlapping arms of one line then cover identical
    /// pixels (no darker anti-aliased seams where they overlap) and neighbouring cells meet exactly.
    /// </summary>
    private static void Snap(RowContent ops, TerminalFonts f, IBrush brush, Rect r)
    {
        var l = f.SnapX(r.X);
        var t = f.SnapY(r.Y);
        var w = Math.Max(1 / f.Scaling, f.SnapX(r.Right) - l);
        var h = Math.Max(1 / f.Scaling, f.SnapY(r.Bottom) - t);
        ops.Fg(brush, new Rect(l, t, w, h));
    }

    private static double Light(TerminalFonts f) => Math.Max(1 / f.Scaling, Math.Round(f.FontSize / 14 * f.Scaling) / f.Scaling);

    private static void Line(RowContent ops, int cp, double x0, double x1, double y0, double y1, IBrush brush, TerminalFonts f)
    {
        var t1 = Light(f);
        var t2 = Math.Max(t1 * 2, t1 + (1 / f.Scaling));
        // centre of the stroke, snapped so that a stroke of t1 starts on a device pixel
        var cx = f.SnapX(((x0 + x1) / 2) - (t1 / 2)) + (t1 / 2);
        var cy = f.SnapY(((y0 + y1) / 2) - (t1 / 2)) + (t1 / 2);

        switch (cp)
        {
            case >= 0x2504 and <= 0x250B or >= 0x254C and <= 0x254F:
                Dashed(ops, cp, x0, x1, y0, y1, cx, cy, t1, t2, brush, f);
                return;
            case >= 0x256D and <= 0x2570:
                Arc(ops, cp, x0, x1, y0, y1, cx, cy, t1, brush);
                return;
            case >= 0x2571 and <= 0x2573:
                Diagonal(ops, cp, x0, x1, y0, y1, t1, brush);
                return;
        }

        var i = (cp - 0x2500) * 4;
        int up = Arms[i] - '0', right = Arms[i + 1] - '0', down = Arms[i + 2] - '0', left = Arms[i + 3] - '0';
        var g = t1; // half distance between the two strokes of a double line (centre to centre = 2*t1)

        double Half(int w) => w switch { 1 => t1 / 2, 2 => t2 / 2, 3 => g + (t1 / 2), _ => 0 };
        double Thick(int w) => w == 2 ? t2 : t1;

        var vPerp = Math.Max(Half(up), Half(down));    // how far horizontal arms must reach past the centre
        var hPerp = Math.Max(Half(left), Half(right)); // how far vertical arms must reach past the centre

        var h = t1 / 2;
        var singleV = (up > 0 || down > 0) && up != 3 && down != 3;     // perpendicular to horizontal arms, no doubles
        var singleH = (left > 0 || right > 0) && left != 3 && right != 3;
        var hpV = Math.Max(up == 2 || down == 2 ? t2 / 2 : 0, h);        // half width of those single strokes
        var hpH = Math.Max(left == 2 || right == 2 ? t2 / 2 : 0, h);

        // horizontal arms
        if (left is 1 or 2)
        {
            var reach = vPerp > 0 ? vPerp : Thick(left) / 2;
            Snap(ops, f, brush, new Rect(x0, cy - (Thick(left) / 2), cx + reach - x0, Thick(left)));
        }
        else if (left == 3)
        {
            var topEnd = singleV ? cx + hpV : up == 3 ? cx - g + h : cx + g + h;
            var botEnd = singleV ? cx + hpV : down == 3 ? cx - g + h : cx + g + h;
            Snap(ops, f, brush, new Rect(x0, cy - g - h, topEnd - x0, t1));
            Snap(ops, f, brush, new Rect(x0, cy + g - h, botEnd - x0, t1));
        }

        if (right is 1 or 2)
        {
            var reach = vPerp > 0 ? vPerp : Thick(right) / 2;
            Snap(ops, f, brush, new Rect(cx - reach, cy - (Thick(right) / 2), x1 - (cx - reach), Thick(right)));
        }
        else if (right == 3)
        {
            var topStart = singleV ? cx - hpV : up == 3 ? cx + g - h : cx - g - h;
            var botStart = singleV ? cx - hpV : down == 3 ? cx + g - h : cx - g - h;
            Snap(ops, f, brush, new Rect(topStart, cy - g - h, x1 - topStart, t1));
            Snap(ops, f, brush, new Rect(botStart, cy + g - h, x1 - botStart, t1));
        }

        // vertical arms
        if (up is 1 or 2)
        {
            var reach = hPerp > 0 ? hPerp : Thick(up) / 2;
            Snap(ops, f, brush, new Rect(cx - (Thick(up) / 2), y0, Thick(up), cy + reach - y0));
        }
        else if (up == 3)
        {
            var lEnd = singleH ? cy + hpH : left == 3 ? cy - g + h : cy + g + h;
            var rEnd = singleH ? cy + hpH : right == 3 ? cy - g + h : cy + g + h;
            Snap(ops, f, brush, new Rect(cx - g - h, y0, t1, lEnd - y0));
            Snap(ops, f, brush, new Rect(cx + g - h, y0, t1, rEnd - y0));
        }

        if (down is 1 or 2)
        {
            var reach = hPerp > 0 ? hPerp : Thick(down) / 2;
            Snap(ops, f, brush, new Rect(cx - (Thick(down) / 2), cy - reach, Thick(down), y1 - (cy - reach)));
        }
        else if (down == 3)
        {
            var lStart = singleH ? cy - hpH : left == 3 ? cy + g - h : cy - g - h;
            var rStart = singleH ? cy - hpH : right == 3 ? cy + g - h : cy - g - h;
            Snap(ops, f, brush, new Rect(cx - g - h, lStart, t1, y1 - lStart));
            Snap(ops, f, brush, new Rect(cx + g - h, rStart, t1, y1 - rStart));
        }
    }

    private static void Dashed(RowContent ops, int cp, double x0, double x1, double y0, double y1, double cx, double cy, double t1, double t2, IBrush brush, TerminalFonts f)
    {
        int dashes;
        bool heavy, horizontal;
        if (cp >= 0x254C)
        {
            dashes = 2;
            heavy = (cp & 1) == 1;
            horizontal = cp < 0x254E;
        }
        else
        {
            var k = cp - 0x2504; // 0..7: h3 light, h3 heavy, v3 light, v3 heavy, h4 light, h4 heavy, v4 light, v4 heavy
            dashes = k < 4 ? 3 : 4;
            heavy = (k & 1) == 1;
            horizontal = (k & 2) == 0;
        }

        var t = heavy ? t2 : t1;
        var len = horizontal ? x1 - x0 : y1 - y0;
        var seg = len / dashes;
        var gap = Math.Max(1 / f.Scaling, seg * 0.35);
        for (var i = 0; i < dashes; i++)
        {
            var s = (i * seg) + (gap / 2);
            if (horizontal)
            {
                var a = f.SnapX(x0 + s);
                var b = f.SnapX(x0 + s + seg - gap);
                ops.Fg(brush, new Rect(a, cy - (t / 2), Math.Max(1 / f.Scaling, b - a), t));
            }
            else
            {
                var a = f.SnapY(y0 + s);
                var b = f.SnapY(y0 + s + seg - gap);
                ops.Fg(brush, new Rect(cx - (t / 2), a, t, Math.Max(1 / f.Scaling, b - a)));
            }
        }
    }

    private static void Arc(RowContent ops, int cp, double x0, double x1, double y0, double y1, double cx, double cy, double t1, IBrush brush)
    {
        var r = Math.Min(x1 - x0, y1 - y0) / 2;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            switch (cp)
            {
                case 0x256D: // ╭ down and right
                    ctx.BeginFigure(new Point(cx, y1), false);
                    ctx.LineTo(new Point(cx, cy + r));
                    ctx.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    ctx.LineTo(new Point(x1, cy));
                    break;
                case 0x256E: // ╮ down and left
                    ctx.BeginFigure(new Point(cx, y1), false);
                    ctx.LineTo(new Point(cx, cy + r));
                    ctx.ArcTo(new Point(cx - r, cy), new Size(r, r), 0, false, SweepDirection.CounterClockwise);
                    ctx.LineTo(new Point(x0, cy));
                    break;
                case 0x256F: // ╯ up and left
                    ctx.BeginFigure(new Point(cx, y0), false);
                    ctx.LineTo(new Point(cx, cy - r));
                    ctx.ArcTo(new Point(cx - r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    ctx.LineTo(new Point(x0, cy));
                    break;
                default: // ╰ up and right
                    ctx.BeginFigure(new Point(cx, y0), false);
                    ctx.LineTo(new Point(cx, cy - r));
                    ctx.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.CounterClockwise);
                    ctx.LineTo(new Point(x1, cy));
                    break;
            }

            ctx.EndFigure(false);
        }

        ops.Geometry(null, new Pen(brush, t1, lineCap: PenLineCap.Flat), geo);
    }

    private static void Diagonal(RowContent ops, int cp, double x0, double x1, double y0, double y1, double t1, IBrush brush)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            if (cp is 0x2571 or 0x2573)
            {
                ctx.BeginFigure(new Point(x1, y0), false);
                ctx.LineTo(new Point(x0, y1));
                ctx.EndFigure(false);
            }

            if (cp is 0x2572 or 0x2573)
            {
                ctx.BeginFigure(new Point(x0, y0), false);
                ctx.LineTo(new Point(x1, y1));
                ctx.EndFigure(false);
            }
        }

        ops.Geometry(null, new Pen(brush, t1, lineCap: PenLineCap.Square), geo);
    }

    private static void Block(RowContent ops, int cp, double x0, double x1, double y0, double y1, IBrush brush, TerminalFonts f)
    {
        var w = x1 - x0;
        var h = y1 - y0;
        double Y(double frac) => f.SnapY(y0 + (h * frac));
        double X(double frac) => f.SnapX(x0 + (w * frac));

        switch (cp)
        {
            case 0x2580: ops.Fg(brush, new Rect(x0, y0, w, Y(0.5) - y0)); return;
            case >= 0x2581 and <= 0x2588:
                {
                    var top = Y(1 - ((cp - 0x2580) / 8.0));
                    ops.Fg(brush, new Rect(x0, top, w, y1 - top));
                    return;
                }

            case >= 0x2589 and <= 0x258F:
                {
                    var right = X((8 - (cp - 0x2588)) / 8.0);
                    ops.Fg(brush, new Rect(x0, y0, right - x0, h));
                    return;
                }

            case 0x2590:
                {
                    var l = X(0.5);
                    ops.Fg(brush, new Rect(l, y0, x1 - l, h));
                    return;
                }

            case >= 0x2591 and <= 0x2593:
                {
                    var alpha = (cp - 0x2590) * 0.25;
                    var c = brush is ISolidColorBrush sb ? sb.Color : Colors.Gray;
                    ops.Fg(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(c, alpha), new Rect(x0, y0, w, h));
                    return;
                }

            case 0x2594: ops.Fg(brush, new Rect(x0, y0, w, Y(1 / 8.0) - y0)); return;
            case 0x2595:
                {
                    var l = X(7 / 8.0);
                    ops.Fg(brush, new Rect(l, y0, x1 - l, h));
                    return;
                }
        }

        // quadrants: UL=1, UR=2, LL=4, LR=8
        var mask = cp switch
        {
            0x2596 => 4, 0x2597 => 8, 0x2598 => 1, 0x2599 => 1 | 4 | 8, 0x259A => 1 | 8, 0x259B => 1 | 2 | 4,
            0x259C => 1 | 2 | 8, 0x259D => 2, 0x259E => 2 | 4, _ => 2 | 4 | 8,
        };
        var mx = X(0.5);
        var my = Y(0.5);
        if ((mask & 1) != 0) ops.Fg(brush, new Rect(x0, y0, mx - x0, my - y0));
        if ((mask & 2) != 0) ops.Fg(brush, new Rect(mx, y0, x1 - mx, my - y0));
        if ((mask & 4) != 0) ops.Fg(brush, new Rect(x0, my, mx - x0, y1 - my));
        if ((mask & 8) != 0) ops.Fg(brush, new Rect(mx, my, x1 - mx, y1 - my));
    }

    private static void Powerline(RowContent ops, int cp, double x0, double x1, double y0, double y1, IBrush brush, TerminalFonts f)
    {
        var t1 = Light(f);
        var cy = (y0 + y1) / 2;
        var geo = new StreamGeometry();
        var filled = (cp & 1) == 0;
        using (var ctx = geo.Open())
        {
            switch (cp)
            {
                case 0xE0B0 or 0xE0B1: // ▶ / ❯
                    ctx.BeginFigure(new Point(x0, y0), filled);
                    ctx.LineTo(new Point(x1, cy));
                    ctx.LineTo(new Point(x0, y1));
                    break;
                case 0xE0B2 or 0xE0B3: // ◀ / ❮
                    ctx.BeginFigure(new Point(x1, y0), filled);
                    ctx.LineTo(new Point(x0, cy));
                    ctx.LineTo(new Point(x1, y1));
                    break;
                case 0xE0B4 or 0xE0B5: // right half circle
                    ctx.BeginFigure(new Point(x0, y0), filled);
                    ctx.ArcTo(new Point(x0, y1), new Size(x1 - x0, (y1 - y0) / 2), 0, false, SweepDirection.Clockwise);
                    break;
                case 0xE0B6 or 0xE0B7: // left half circle
                    ctx.BeginFigure(new Point(x1, y0), filled);
                    ctx.ArcTo(new Point(x1, y1), new Size(x1 - x0, (y1 - y0) / 2), 0, false, SweepDirection.CounterClockwise);
                    break;
                case 0xE0B8 or 0xE0B9: // lower-left triangle / diagonal
                    ctx.BeginFigure(new Point(x0, y0), filled);
                    ctx.LineTo(new Point(x1, y1));
                    if (filled) ctx.LineTo(new Point(x0, y1));
                    break;
                case 0xE0BA or 0xE0BB: // lower-right triangle
                    ctx.BeginFigure(new Point(x1, y0), filled);
                    ctx.LineTo(new Point(x0, y1));
                    if (filled) ctx.LineTo(new Point(x1, y1));
                    break;
                case 0xE0BC or 0xE0BD: // upper-left triangle
                    ctx.BeginFigure(new Point(x0, y1), filled);
                    ctx.LineTo(new Point(x1, y0));
                    if (filled) ctx.LineTo(new Point(x0, y0));
                    break;
                default: // upper-right triangle
                    ctx.BeginFigure(new Point(x0, y0), filled);
                    ctx.LineTo(new Point(x1, y1));
                    if (filled) ctx.LineTo(new Point(x1, y0));
                    break;
            }

            ctx.EndFigure(filled);
        }

        if (filled)
        {
            ops.Geometry(brush, null, geo);
        }
        else
        {
            ops.Geometry(null, new Pen(brush, t1, lineCap: PenLineCap.Square, lineJoin: PenLineJoin.Miter), geo);
        }
    }
}
