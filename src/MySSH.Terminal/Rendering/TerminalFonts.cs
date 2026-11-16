using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace MySSH.Terminal.Rendering;

/// <summary>
/// A resolved glyph for one code point: the face that has it, its glyph id, its advance in DIPs and (for fallback
/// faces) its ink extent relative to the pen position — colour emoji bitmaps are drawn wider than their advance.
/// </summary>
internal readonly record struct GlyphEntry(GlyphTypeface Face, ushort Glyph, double Advance, double InkLeft, double InkWidth);

/// <summary>A shaped grapheme cluster (combining marks, ZWJ emoji, flags).</summary>
internal sealed class ClusterEntry(GlyphTypeface face, GlyphInfo[] glyphs, double advance, double inkLeft, double inkWidth)
{
    public GlyphTypeface Face { get; } = face;
    public GlyphInfo[] Glyphs { get; } = glyphs;
    public double Advance { get; } = advance;
    public double InkLeft { get; } = inkLeft;
    public double InkWidth { get; } = inkWidth;
}

/// <summary>
/// Cell metrics and glyph lookup with caching. ASCII hits a flat array; other code points and clusters are
/// resolved once (primary face → configured fallback family → platform font fallback) and memoised, so
/// steady-state rendering never shapes text.
/// </summary>
internal sealed class TerminalFonts
{
    private const int StyleCount = 4; // bit 0 = bold, bit 1 = italic
    private const int MaxCacheEntries = 16384;

    private readonly GlyphTypeface[] _primary = new GlyphTypeface[StyleCount];
    private readonly GlyphTypeface?[] _fallbackFace = new GlyphTypeface?[StyleCount];
    private readonly GlyphEntry?[][] _ascii = new GlyphEntry?[StyleCount][];
    private readonly Dictionary<long, GlyphEntry> _singles = [];
    private readonly Dictionary<(string, int), ClusterEntry> _clusters = [];
    private readonly FontFamily _family;
    private readonly FontFamily? _fallbackFamily;
    private readonly CultureInfo _culture;

    public TerminalFonts(FontFamily family, FontFamily? fallbackFamily, double fontSize, double renderScaling)
    {
        _family = family;
        _fallbackFamily = fallbackFamily;
        _culture = CultureInfo.CurrentUICulture;
        FontSize = fontSize;
        Scaling = renderScaling <= 0 ? 1 : renderScaling;

        for (var s = 0; s < StyleCount; s++)
        {
            var tf = new Typeface(family, (s & 2) != 0 ? FontStyle.Italic : FontStyle.Normal, (s & 1) != 0 ? FontWeight.Bold : FontWeight.Normal);
            _primary[s] = tf.GlyphTypeface;
            _ascii[s] = new GlyphEntry?[128];
            if (fallbackFamily is not null)
            {
                var ft = new Typeface(fallbackFamily, tf.Style, tf.Weight);
                _fallbackFace[s] = FontManager.Current.TryGetGlyphTypeface(ft, out var g) ? g : null;
            }
        }

        var regular = _primary[0];
        var m = regular.Metrics;
        double em = m.DesignEmHeight == 0 ? 1000 : m.DesignEmHeight;
        var unit = fontSize / em;
        var ascent = Math.Abs(m.Ascent) * unit;
        var descent = Math.Abs(m.Descent) * unit;
        var gap = Math.Max(0, m.LineGap) * unit;

        double advance = fontSize * 0.6;
        var mGlyph = regular.CharacterToGlyphMap.GetGlyph('M');
        if (mGlyph != 0 && regular.TryGetHorizontalGlyphAdvance(mGlyph, out var adv))
        {
            advance = adv * unit;
        }

        CellWidth = advance;
        CellHeight = Math.Max(1, Math.Ceiling((ascent + descent + gap) * Scaling) / Scaling);
        Baseline = SnapY(((CellHeight - (ascent + descent)) / 2) + ascent);

        var thickness = m.UnderlineThickness != 0 ? Math.Abs(m.UnderlineThickness) * unit : fontSize / 14;
        LineThickness = Math.Max(1 / Scaling, Math.Round(thickness * Scaling) / Scaling);
        var ulPos = m.UnderlinePosition != 0 ? Math.Abs(m.UnderlinePosition) * unit : descent / 2;
        UnderlineY = Math.Min(CellHeight - LineThickness, SnapY(Baseline + Math.Max(1 / Scaling, ulPos)));
        StrikeY = SnapY(Baseline - (ascent * 0.3));
    }

    public double FontSize { get; }
    public double Scaling { get; }
    public double CellWidth { get; }
    public double CellHeight { get; }
    public double Baseline { get; }
    public double LineThickness { get; }
    public double UnderlineY { get; }
    public double StrikeY { get; }
    public GlyphTypeface PrimaryFace => _primary[0];

    public bool IsPrimary(GlyphTypeface face) => Array.IndexOf(_primary, face) >= 0;

    public double SnapX(double x) => Math.Round(x * Scaling) / Scaling;

    public double SnapY(double y) => Math.Round(y * Scaling) / Scaling;

    /// <summary>Left edge of column <paramref name="col"/>, snapped to device pixels so neighbouring rects never leave seams.</summary>
    public double ColumnX(int col) => SnapX(col * CellWidth);

    public static int StyleIndex(bool bold, bool italic) => (bold ? 1 : 0) | (italic ? 2 : 0);

    public GlyphEntry Get(int codePoint, int style)
    {
        if (codePoint is >= 0 and < 128)
        {
            var row = _ascii[style];
            if (row[codePoint] is { } hit)
            {
                return hit;
            }

            var e = Resolve(codePoint, style);
            row[codePoint] = e;
            return e;
        }

        var key = ((long)codePoint << 2) | (uint)style;
        if (_singles.TryGetValue(key, out var found))
        {
            return found;
        }

        if (_singles.Count > MaxCacheEntries)
        {
            _singles.Clear();
        }

        var entry = Resolve(codePoint, style);
        _singles[key] = entry;
        return entry;
    }

    public ClusterEntry GetCluster(string text, int style)
    {
        if (_clusters.TryGetValue((text, style), out var found))
        {
            return found;
        }

        if (_clusters.Count > MaxCacheEntries)
        {
            _clusters.Clear();
        }

        var entry = Shape(text, style);
        _clusters[(text, style)] = entry;
        return entry;
    }

    private GlyphEntry Resolve(int codePoint, int style)
    {
        var face = FaceFor(codePoint, style);
        var glyph = face.CharacterToGlyphMap.GetGlyph(codePoint);
        var advance = AdvanceOf(face, glyph);
        var (inkLeft, inkWidth) = IsPrimary(face) ? (0, advance) : Ink(face, [new GlyphInfo(glyph, 0, advance)], advance);
        return new GlyphEntry(face, glyph, advance, inkLeft, inkWidth);
    }

    private GlyphTypeface FaceFor(int codePoint, int style)
    {
        var primary = _primary[style];
        if (primary.CharacterToGlyphMap.ContainsGlyph(codePoint))
        {
            return primary;
        }

        if (_fallbackFace[style] is { } fb && fb.CharacterToGlyphMap.ContainsGlyph(codePoint))
        {
            return fb;
        }

        if (FontManager.Current.TryMatchCharacter(codePoint, primary.Style, primary.Weight, FontStretch.Normal, _fallbackFamily, _culture, out var tf)
            && tf.GlyphTypeface is { } matched && matched.CharacterToGlyphMap.ContainsGlyph(codePoint))
        {
            return matched;
        }

        return primary; // .notdef box: visible, and keeps the grid
    }

    private ClusterEntry Shape(string text, int style)
    {
        var baseCp = char.ConvertToUtf32(text, 0);
        var face = FaceFor(baseCp, style);
        var shaped = ShapeWith(face, text);
        if (Array.Exists(shaped, g => g.GlyphIndex == 0) && face == _primary[style])
        {
            // e.g. a combining mark the primary font lacks: let the platform pick a face for the whole cluster
            for (var i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
            {
                var cp = char.ConvertToUtf32(text, i);
                var other = FaceFor(cp, style);
                if (other != face)
                {
                    var retry = ShapeWith(other, text);
                    if (!Array.Exists(retry, g => g.GlyphIndex == 0))
                    {
                        face = other;
                        shaped = retry;
                        break;
                    }
                }
            }
        }

        double advance = 0;
        foreach (var g in shaped)
        {
            advance += g.GlyphAdvance;
        }

        var (inkLeft, inkWidth) = Ink(face, shaped, advance);
        return new ClusterEntry(face, shaped, advance, inkLeft, inkWidth);
    }

    /// <summary>Horizontal ink extent of a glyph sequence drawn at the pen origin (measured once, then cached).</summary>
    private (double Left, double Width) Ink(GlyphTypeface face, GlyphInfo[] glyphs, double advance)
    {
        if (glyphs.Length == 0)
        {
            return (0, advance);
        }

        using var run = new GlyphRun(face, FontSize, new string(' ', glyphs.Length).AsMemory(), glyphs, new Avalonia.Point(0, 0));
        var ink = run.InkBounds;
        return ink.Width > 0 ? (Math.Min(0, ink.X), Math.Max(ink.Right, advance) - Math.Min(0, ink.X)) : (0, advance);
    }

    private GlyphInfo[] ShapeWith(GlyphTypeface face, string text)
    {
        using var buffer = TextShaper.Current.ShapeText(text.AsMemory(), new TextShaperOptions(face, FontSize, 0, _culture));
        var result = new GlyphInfo[buffer.Length];
        for (var i = 0; i < buffer.Length; i++)
        {
            result[i] = buffer[i];
        }

        return result;
    }

    private double AdvanceOf(GlyphTypeface face, ushort glyph)
    {
        double em = face.Metrics.DesignEmHeight == 0 ? 1000 : face.Metrics.DesignEmHeight;
        return face.TryGetHorizontalGlyphAdvance(glyph, out var adv) ? adv * FontSize / em : CellWidth;
    }
}
