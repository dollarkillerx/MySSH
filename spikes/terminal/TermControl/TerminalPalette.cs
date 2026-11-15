using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace TermControl;

/// <summary>Carbon Gray 100 based terminal palette (design doc §9.1) + standard xterm 256-color cube.</summary>
public sealed class TerminalPalette
{
    public Color Background { get; init; } = Color.Parse("#161616");
    public Color Foreground { get; init; } = Color.Parse("#f4f4f4");
    public Color Cursor { get; init; } = Color.Parse("#ffffff");
    public Color Selection { get; init; } = Color.FromArgb(0x66, 0x45, 0x89, 0xff); // blue 50 @ 40%
    public Color PreeditBackground { get; init; } = Color.Parse("#393939");
    public Color PreeditUnderline { get; init; } = Color.Parse("#4589ff");

    // IBM palette (black, red 50, green 40, yellow 30, blue 50, purple 40, cyan 40, gray 20) + brights
    readonly Color[] _ansi16 =
    [
        Color.Parse("#262626"), Color.Parse("#fa4d56"), Color.Parse("#42be65"), Color.Parse("#f1c21b"),
        Color.Parse("#4589ff"), Color.Parse("#be95ff"), Color.Parse("#33b1ff"), Color.Parse("#c6c6c6"),
        Color.Parse("#6f6f6f"), Color.Parse("#ff8389"), Color.Parse("#6fdc8c"), Color.Parse("#fddc69"),
        Color.Parse("#78a9ff"), Color.Parse("#d4bbff"), Color.Parse("#82cfff"), Color.Parse("#f4f4f4"),
    ];

    readonly Dictionary<uint, IImmutableSolidColorBrush> _brushes = new();

    public XTerm.Options.ThemeOptions ToThemeOptions()
    {
        static string H(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        return new XTerm.Options.ThemeOptions
        {
            Background = H(Background), Foreground = H(Foreground), Cursor = H(Cursor),
            Black = H(_ansi16[0]), Red = H(_ansi16[1]), Green = H(_ansi16[2]), Yellow = H(_ansi16[3]),
            Blue = H(_ansi16[4]), Magenta = H(_ansi16[5]), Cyan = H(_ansi16[6]), White = H(_ansi16[7]),
            BrightBlack = H(_ansi16[8]), BrightRed = H(_ansi16[9]), BrightGreen = H(_ansi16[10]), BrightYellow = H(_ansi16[11]),
            BrightBlue = H(_ansi16[12]), BrightMagenta = H(_ansi16[13]), BrightCyan = H(_ansi16[14]), BrightWhite = H(_ansi16[15]),
        };
    }

    public Color Indexed(int i)
    {
        if (i < 16) return _ansi16[i];
        if (i < 232)
        {
            i -= 16;
            static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            return Color.FromRgb(Level(i / 36), Level(i / 6 % 6), Level(i % 6));
        }
        var g = (byte)(8 + (i - 232) * 10);
        return Color.FromRgb(g, g, g);
    }

    public IImmutableSolidColorBrush Brush(Color c)
    {
        var key = c.ToUInt32();
        if (!_brushes.TryGetValue(key, out var b)) _brushes[key] = b = new ImmutableSolidColorBrush(c);
        return b;
    }
}
