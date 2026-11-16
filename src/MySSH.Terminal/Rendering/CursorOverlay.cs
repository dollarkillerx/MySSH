using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MySSH.Terminal.Rendering;

/// <summary>Accumulated render cost, read by the benchmark and the diagnostics overlay.</summary>
internal sealed class RenderStats
{
    public int RowsRendered { get; private set; }
    public int RowsBuilt { get; private set; }
    public TimeSpan DrawTime { get; set; }
    public TimeSpan RowTime { get; private set; }
    public int OverlayRenders { get; private set; }
    public TimeSpan OverlayTime { get; private set; }

    public void Reset()
    {
        RowsRendered = 0;
        RowsBuilt = 0;
        DrawTime = TimeSpan.Zero;
        RowTime = TimeSpan.Zero;
        OverlayRenders = 0;
        OverlayTime = TimeSpan.Zero;
    }

    public void OnRowBuilt() => RowsBuilt++;

    public void OnRowRendered(TimeSpan t)
    {
        RowsRendered++;
        RowTime += t;
    }

    public void OnOverlayRendered(TimeSpan t)
    {
        OverlayRenders++;
        OverlayTime += t;
    }
}

/// <summary>
/// Cursor and inline IME preedit, drawn above the rows so that blinking or moving the cursor never
/// re-renders a row.
/// </summary>
internal sealed class CursorOverlay : Control
{
    private readonly TerminalView _owner;
    private GlyphRun? _cursorGlyph;

    public CursorOverlay(TerminalView owner)
    {
        _owner = owner;
        IsHitTestVisible = false;
        Focusable = false;
    }

    public override void Render(DrawingContext context)
    {
        var start = Stopwatch.GetTimestamp();
        _cursorGlyph?.Dispose();
        _cursorGlyph = null;
        if (_owner.Builder is { } builder && _owner.Fonts is { } fonts)
        {
            DrawCursor(context, builder, fonts);
        }

        _owner.Stats.OnOverlayRendered(Stopwatch.GetElapsedTime(start));
    }

    private void DrawCursor(DrawingContext ctx, RowBuilder builder, TerminalFonts f)
    {
        var engine = _owner.Engine;
        var buf = engine.Buffer;
        var row = buf.YBase + buf.Y - buf.YDisp;
        if (row < 0 || row >= engine.Rows)
        {
            return;
        }

        var p = builder.Palette;
        var col = Math.Min(buf.X, engine.Cols - 1);
        var y = row * f.CellHeight;

        if (_owner.Preedit is { Length: > 0 } preedit)
        {
            var ft = new FormattedText(preedit, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(_owner.EffectiveFontFamily), f.FontSize, p.Brush(p.Foreground));
            var x = f.ColumnX(col);
            var w = Math.Max(f.CellWidth, Math.Ceiling(ft.WidthIncludingTrailingWhitespace));
            var rect = new Rect(x, y, w, f.CellHeight);
            ctx.FillRectangle(p.Brush(p.Theme.PreeditBackground.ToUInt32()), rect);
            ctx.DrawText(ft, new Point(x, y + f.Baseline - ft.Baseline));
            var ul = Math.Max(2 / f.Scaling, f.LineThickness);
            ctx.FillRectangle(p.Brush(p.Theme.PreeditUnderline.ToUInt32()), new Rect(x, y + f.CellHeight - ul, w, ul));
            // caret inside the composition
            var caret = _owner.PreeditCursor is { } pc && pc >= 0 && pc < preedit.Length
                ? new FormattedText(preedit[..pc], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(_owner.EffectiveFontFamily), f.FontSize, Brushes.Transparent).WidthIncludingTrailingWhitespace
                : w;
            ctx.FillRectangle(p.Brush(p.Cursor), new Rect(x + caret, y, Math.Max(1, f.LineThickness), f.CellHeight));
            return;
        }

        if (!engine.CursorVisible || !_owner.CursorBlinkVisible || _owner.IsSessionEnded)
        {
            return;
        }

        var line = buf.GetLine(buf.YBase + buf.Y);
        if (line is null)
        {
            return;
        }

        var cell = line[col];
        var width = cell.Width == 2 ? 2 : 1;
        var x0 = f.ColumnX(col);
        var x1 = f.ColumnX(col + width);
        var cursorBrush = p.Brush(p.Cursor);
        var t = Math.Max(f.LineThickness, 2 / f.Scaling);

        if (!_owner.IsFocused)
        {
            ctx.DrawRectangle(new Pen(cursorBrush, 1 / f.Scaling), new Rect(x0, y, x1 - x0, f.CellHeight).Deflate(0.5 / f.Scaling));
            return;
        }

        switch (_owner.EffectiveCursorStyle)
        {
            case TerminalCursorStyle.Bar:
                ctx.FillRectangle(cursorBrush, new Rect(x0, y, t, f.CellHeight));
                break;
            case TerminalCursorStyle.Underline:
                ctx.FillRectangle(cursorBrush, new Rect(x0, y + f.CellHeight - t, x1 - x0, t));
                break;
            default:
                ctx.FillRectangle(cursorBrush, new Rect(x0, y, x1 - x0, f.CellHeight));
                builder.ResolveCell(cell, out _, out _, out var style);
                _cursorGlyph = builder.BuildCell(cell, col * f.CellWidth, style);
                if (_cursorGlyph is not null)
                {
                    using (ctx.PushTransform(Matrix.CreateTranslation(0, y)))
                    {
                        ctx.DrawGlyphRun(p.Brush(p.CursorText), _cursorGlyph);
                    }
                }

                break;
        }
    }
}
