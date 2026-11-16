using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using XTerm.Buffer;

namespace MySSH.Terminal.Rendering;

/// <summary>
/// One terminal row as its own visual. The compositor keeps its recorded drawing, so rows that did not change
/// cost nothing per frame; a scroll only moves visuals (the line they show keeps its visual).
/// </summary>
internal sealed class RowVisual : Control
{
    private readonly TerminalView _owner;
    private readonly RowContent _content = new();
    private readonly TranslateTransform _offset = new();
    private bool _dirty = true;
    private int _slot = -1;

    public RowVisual(TerminalView owner)
    {
        _owner = owner;
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = false;
        RenderTransform = _offset;
        RenderTransformOrigin = Avalonia.RelativePoint.TopLeft;
    }

    /// <summary>The engine line this visual shows (null = unused).</summary>
    public BufferLine? Line { get; set; }

    public long LineVersion { get; set; }
    public int Epoch { get; set; } = -1;
    public RowDeco Deco { get; set; } = RowDeco.None;

    /// <summary>Viewport row shown. Moving a row only changes its transform, so its recorded drawing is kept.</summary>
    public int Slot
    {
        get => _slot;
        set
        {
            _slot = value;
            var y = value < 0 ? -10000 : value * (_owner.Fonts?.CellHeight ?? 0);
            if (_offset.Y != y)
            {
                _offset.Y = y;
            }
        }
    }

    public long AbsRow { get; set; }
    public bool InUse { get; set; }
    public int PoolIndex { get; init; }
    public int BuildCount { get; private set; }

    public void MarkDirty()
    {
        _dirty = true;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var start = Stopwatch.GetTimestamp();
        if (_dirty)
        {
            _dirty = false;
            if (Line is { } line && _owner.Builder is { } builder)
            {
                builder.Build(line, _owner.Columns, Deco, _owner.HitsForRow(AbsRow), _content);
                BuildCount++;
                _owner.Stats.OnRowBuilt();
            }
            else
            {
                _content.Clear();
            }
        }

        var drawStart = Stopwatch.GetTimestamp();
        _content.Draw(context);
        _owner.Stats.DrawTime += Stopwatch.GetElapsedTime(drawStart);
        _owner.Stats.OnRowRendered(Stopwatch.GetElapsedTime(start));
    }

    public void ReleaseContent() => _content.Clear();
}
