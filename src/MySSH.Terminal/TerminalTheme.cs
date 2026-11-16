using System.Globalization;
using Avalonia.Media;

namespace MySSH.Terminal;

/// <summary>
/// Colours used by <see cref="TerminalView"/>. Every colour can be overridden; unset values in a custom
/// theme are not "inherited" — start from <see cref="CarbonDark"/> / <see cref="CarbonLight"/> with
/// <c>with</c>-style copies via <see cref="Clone"/> and change what you need.
/// </summary>
/// <remarks>
/// When <see cref="TerminalView.ColorTheme"/> is null the view builds a theme from the Carbon tokens of the current
/// <c>ThemeVariant</c> (Gray 100 for dark, White for light), so the terminal follows the app theme.
/// </remarks>
public sealed class TerminalTheme
{
    private Color[] _ansi = new Color[16];

    /// <summary>Default cell background (Carbon <c>background</c>).</summary>
    public Color Background { get; set; }

    /// <summary>Default text colour (Carbon <c>text-primary</c>).</summary>
    public Color Foreground { get; set; }

    /// <summary>Cursor fill colour.</summary>
    public Color Cursor { get; set; }

    /// <summary>Colour of the character under a block cursor.</summary>
    public Color CursorText { get; set; }

    /// <summary>Selection background (Carbon <c>highlight</c>).</summary>
    public Color SelectionBackground { get; set; }

    /// <summary>Optional selection text colour; null keeps each cell's own foreground.</summary>
    public Color? SelectionForeground { get; set; }

    /// <summary>Background of search hits.</summary>
    public Color SearchMatchBackground { get; set; }

    /// <summary>Text colour of search hits.</summary>
    public Color SearchMatchForeground { get; set; }

    /// <summary>Background of the current search hit.</summary>
    public Color SearchCurrentBackground { get; set; }

    /// <summary>Text colour of the current search hit.</summary>
    public Color SearchCurrentForeground { get; set; }

    /// <summary>Underline colour of links (OSC 8 and hovered URLs).</summary>
    public Color Link { get; set; }

    /// <summary>Background behind inline IME composition text.</summary>
    public Color PreeditBackground { get; set; }

    /// <summary>Underline below inline IME composition text.</summary>
    public Color PreeditUnderline { get; set; }

    /// <summary>
    /// Minimum WCAG contrast ratio between a cell's text and its background; text below it is darkened or
    /// lightened at render time. 1 disables the adjustment. Light themes use 3 so that "bright yellow on default"
    /// output written for dark terminals stays readable.
    /// </summary>
    public double MinimumContrastRatio { get; set; } = 1;

    /// <summary>The 16 ANSI colours (0–7 normal, 8–15 bright). Indices 16–255 are the standard xterm cube/ramp.</summary>
    public IReadOnlyList<Color> Ansi
    {
        get => _ansi;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count != 16)
            {
                throw new ArgumentException("Exactly 16 ANSI colours are required.", nameof(value));
            }

            _ansi = [.. value];
        }
    }

    /// <summary>True for themes with a light background (used for contrast decisions).</summary>
    public bool IsLight => RelativeLuminance(Background) > 0.5;

    /// <summary>Carbon Gray 100 (dark) terminal theme.</summary>
    public static TerminalTheme CarbonDark => new()
    {
        Background = Hex("#161616"),
        Foreground = Hex("#f4f4f4"),
        Cursor = Hex("#f4f4f4"),
        CursorText = Hex("#161616"),
        SelectionBackground = Hex("#0043ce"),
        SearchMatchBackground = Hex("#684e00"),
        SearchMatchForeground = Hex("#f4f4f4"),
        SearchCurrentBackground = Hex("#ff832b"),
        SearchCurrentForeground = Hex("#161616"),
        Link = Hex("#78a9ff"),
        PreeditBackground = Hex("#393939"),
        PreeditUnderline = Hex("#4589ff"),
        MinimumContrastRatio = 1,
        // IBM palette: black, red 50, green 40, yellow 30, blue 50, purple 40, cyan 40, gray 20 + brights
        _ansi =
        [
            Hex("#262626"), Hex("#fa4d56"), Hex("#42be65"), Hex("#f1c21b"),
            Hex("#4589ff"), Hex("#be95ff"), Hex("#33b1ff"), Hex("#c6c6c6"),
            Hex("#6f6f6f"), Hex("#ff8389"), Hex("#6fdc8c"), Hex("#fddc69"),
            Hex("#78a9ff"), Hex("#d4bbff"), Hex("#82cfff"), Hex("#ffffff"),
        ],
    };

    /// <summary>Carbon White (light) terminal theme: 60/70 grades so ANSI colours stay readable on white.</summary>
    public static TerminalTheme CarbonLight => new()
    {
        Background = Hex("#ffffff"),
        Foreground = Hex("#161616"),
        Cursor = Hex("#161616"),
        CursorText = Hex("#ffffff"),
        SelectionBackground = Hex("#d0e2ff"),
        SearchMatchBackground = Hex("#fddc69"),
        SearchMatchForeground = Hex("#161616"),
        SearchCurrentBackground = Hex("#ff832b"),
        SearchCurrentForeground = Hex("#161616"),
        Link = Hex("#0f62fe"),
        PreeditBackground = Hex("#e0e0e0"),
        PreeditUnderline = Hex("#0f62fe"),
        MinimumContrastRatio = 3,
        // black = gray 100, red 60, green 60, yellow 60 (dark ochre), blue 60, purple 60, cyan 60, "white" = gray 50
        _ansi =
        [
            Hex("#161616"), Hex("#da1e28"), Hex("#198038"), Hex("#8e6a00"),
            Hex("#0f62fe"), Hex("#8a3ffc"), Hex("#0072c3"), Hex("#8d8d8d"),
            Hex("#6f6f6f"), Hex("#a2191f"), Hex("#0e6027"), Hex("#684e00"),
            Hex("#0043ce"), Hex("#6929c4"), Hex("#00539a"), Hex("#c6c6c6"),
        ],
    };

    /// <summary>Returns an independent copy.</summary>
    public TerminalTheme Clone()
    {
        var copy = (TerminalTheme)MemberwiseClone();
        copy._ansi = [.. _ansi];
        return copy;
    }

    /// <summary>The 256-colour palette entry <paramref name="index"/> (0–255).</summary>
    public Color Indexed(int index)
    {
        if (index < 16)
        {
            return _ansi[Math.Max(0, index)];
        }

        if (index < 232)
        {
            index -= 16;
            static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + (v * 40));
            return Color.FromRgb(Level(index / 36), Level(index / 6 % 6), Level(index % 6));
        }

        var g = (byte)(8 + ((Math.Min(index, 255) - 232) * 10));
        return Color.FromRgb(g, g, g);
    }

    internal XTerm.Options.ThemeOptions ToEngineTheme()
    {
        return new XTerm.Options.ThemeOptions
        {
            Background = H(Background), Foreground = H(Foreground), Cursor = H(Cursor), CursorAccent = H(CursorText),
            Selection = H(SelectionBackground),
            Black = H(_ansi[0]), Red = H(_ansi[1]), Green = H(_ansi[2]), Yellow = H(_ansi[3]),
            Blue = H(_ansi[4]), Magenta = H(_ansi[5]), Cyan = H(_ansi[6]), White = H(_ansi[7]),
            BrightBlack = H(_ansi[8]), BrightRed = H(_ansi[9]), BrightGreen = H(_ansi[10]), BrightYellow = H(_ansi[11]),
            BrightBlue = H(_ansi[12]), BrightMagenta = H(_ansi[13]), BrightCyan = H(_ansi[14]), BrightWhite = H(_ansi[15]),
        };

        static string H(Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:x2}{c.G:x2}{c.B:x2}");
    }

    internal static Color Hex(string s) => Color.Parse(s);

    /// <summary>WCAG relative luminance (0 = black, 1 = white).</summary>
    internal static double RelativeLuminance(Color c)
    {
        static double Ch(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Ch(c.R)) + (0.7152 * Ch(c.G)) + (0.0722 * Ch(c.B));
    }

    internal static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Moves <paramref name="fg"/> towards black or white until it reaches <paramref name="ratio"/> against <paramref name="bg"/>.</summary>
    internal static Color EnsureContrast(Color fg, Color bg, double ratio)
    {
        if (ratio <= 1 || ContrastRatio(fg, bg) >= ratio)
        {
            return fg;
        }

        var towardsBlack = RelativeLuminance(bg) > 0.5;
        var target = towardsBlack ? Colors.Black : Colors.White;
        var best = target;
        for (var t = 0.1; t <= 1.0001; t += 0.1)
        {
            var c = Lerp(fg, target, t);
            if (ContrastRatio(c, bg) >= ratio)
            {
                best = c;
                break;
            }
        }

        return Color.FromArgb(fg.A, best.R, best.G, best.B);
    }

    internal static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + ((b.A - a.A) * t)),
        (byte)Math.Round(a.R + ((b.R - a.R) * t)),
        (byte)Math.Round(a.G + ((b.G - a.G) * t)),
        (byte)Math.Round(a.B + ((b.B - a.B) * t)));
}
