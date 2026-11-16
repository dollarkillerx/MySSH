using Avalonia;
using Avalonia.Media;

namespace MySSH.Terminal.Rendering;

/// <summary>
/// The recorded drawing of one terminal row: background rects, batched glyph runs, foreground rects
/// (box drawing, underlines) and the occasional geometry. Built once per row change and replayed on render,
/// so moving a row (scrolling) never re-reads cells or re-creates glyph runs.
/// </summary>
internal sealed class RowContent
{
    private readonly List<(IBrush Brush, Rect Rect)> _bg = [];
    private readonly List<(IBrush Brush, GlyphRun Run, Matrix? Transform)> _runs = [];
    private readonly List<(IBrush Brush, Rect Rect)> _fg = [];
    private readonly List<(IBrush? Fill, IPen? Pen, Geometry Geometry)> _geo = [];

    public int GlyphRunCount => _runs.Count;

    public bool IsEmpty => _bg.Count == 0 && _runs.Count == 0 && _fg.Count == 0 && _geo.Count == 0;

    public void Clear()
    {
        foreach (var r in _runs)
        {
            r.Run.Dispose();
        }

        _bg.Clear();
        _runs.Clear();
        _fg.Clear();
        _geo.Clear();
    }

    public void Bg(IBrush brush, Rect rect) => _bg.Add((brush, rect));

    public void Fg(IBrush brush, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        // merge with the previous rect when it continues it horizontally (box-drawing lines, underlines)
        if (_fg.Count > 0)
        {
            var (b, last) = _fg[^1];
            if (ReferenceEquals(b, brush) && Math.Abs(last.Y - rect.Y) < 0.01 && Math.Abs(last.Height - rect.Height) < 0.01
                && rect.X >= last.X - 0.01 && rect.X <= last.Right + 0.01)
            {
                _fg[^1] = (b, new Rect(last.X, last.Y, Math.Max(last.Right, rect.Right) - last.X, last.Height));
                return;
            }
        }

        _fg.Add((brush, rect));
    }

    public void Run(IBrush brush, GlyphRun run, Matrix? transform = null) => _runs.Add((brush, run, transform));

    public void Geometry(IBrush? fill, IPen? pen, Geometry geometry) => _geo.Add((fill, pen, geometry));

    public void Draw(DrawingContext ctx)
    {
        foreach (var (brush, rect) in _bg)
        {
            ctx.FillRectangle(brush, rect);
        }

        foreach (var (brush, rect) in _fg)
        {
            ctx.FillRectangle(brush, rect);
        }

        foreach (var (fill, pen, geometry) in _geo)
        {
            ctx.DrawGeometry(fill, pen, geometry);
        }

        foreach (var (brush, run, transform) in _runs)
        {
            if (transform is { } m)
            {
                using (ctx.PushTransform(m))
                {
                    ctx.DrawGlyphRun(brush, run);
                }
            }
            else
            {
                ctx.DrawGlyphRun(brush, run);
            }
        }
    }
}
