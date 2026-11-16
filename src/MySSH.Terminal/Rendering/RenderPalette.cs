using Avalonia.Media;
using Avalonia.Media.Immutable;
using XTerm.Common;

namespace MySSH.Terminal.Rendering;

/// <summary>
/// Flattened colours for the hot path: the engine palette (theme + OSC 4/10/11 changes) as ARGB, theme extras,
/// a brush cache and the contrast-adjustment cache.
/// </summary>
internal sealed class RenderPalette
{
    private readonly uint[] _indexed = new uint[256];
    private readonly Dictionary<uint, IImmutableSolidColorBrush> _brushes = [];
    private readonly Dictionary<ulong, uint> _contrast = [];

    public RenderPalette(TerminalTheme theme, ColorPalette engine)
    {
        Theme = theme;
        for (var i = 0; i < 256; i++)
        {
            _indexed[i] = 0xFF000000u | (uint)engine[i];
        }

        Foreground = 0xFF000000u | (uint)engine.Foreground;
        Background = 0xFF000000u | (uint)engine.Background;
        Cursor = 0xFF000000u | (uint)engine.Cursor;
        CursorText = theme.CursorText.ToUInt32();
        SelectionBg = theme.SelectionBackground.ToUInt32();
        SelectionFg = theme.SelectionForeground?.ToUInt32();
        MatchBg = theme.SearchMatchBackground.ToUInt32();
        MatchFg = theme.SearchMatchForeground.ToUInt32();
        CurrentBg = theme.SearchCurrentBackground.ToUInt32();
        CurrentFg = theme.SearchCurrentForeground.ToUInt32();
        Link = theme.Link.ToUInt32();
        MinimumContrast = theme.MinimumContrastRatio;
    }

    public TerminalTheme Theme { get; }
    public uint Foreground { get; }
    public uint Background { get; }
    public uint Cursor { get; }
    public uint CursorText { get; }
    public uint SelectionBg { get; }
    public uint? SelectionFg { get; }
    public uint MatchBg { get; }
    public uint MatchFg { get; }
    public uint CurrentBg { get; }
    public uint CurrentFg { get; }
    public uint Link { get; }
    public double MinimumContrast { get; }

    public uint Indexed(int i) => _indexed[i & 0xff];

    public IImmutableSolidColorBrush Brush(uint argb)
    {
        if (!_brushes.TryGetValue(argb, out var b))
        {
            if (_brushes.Count > 4096)
            {
                _brushes.Clear();
            }

            _brushes[argb] = b = new ImmutableSolidColorBrush(argb);
        }

        return b;
    }

    public uint Contrast(uint fg, uint bg)
    {
        if (MinimumContrast <= 1)
        {
            return fg;
        }

        var key = ((ulong)fg << 32) | bg;
        if (!_contrast.TryGetValue(key, out var adjusted))
        {
            if (_contrast.Count > 4096)
            {
                _contrast.Clear();
            }

            adjusted = TerminalTheme.EnsureContrast(Color.FromUInt32(fg), Color.FromUInt32(bg), MinimumContrast).ToUInt32();
            _contrast[key] = adjusted;
        }

        return adjusted;
    }

    public static uint Blend(uint a, uint b, double t) => TerminalTheme.Lerp(Color.FromUInt32(a), Color.FromUInt32(b), t).ToUInt32();
}
