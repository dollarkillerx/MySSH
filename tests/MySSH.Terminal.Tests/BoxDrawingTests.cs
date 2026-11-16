using Avalonia.Headless.XUnit;

namespace MySSH.Terminal.Tests;

/// <summary>Pixel checks: procedural box drawing has no gaps between cells or rows.</summary>
public class BoxDrawingTests
{
    private const string E = Harness.E;

    private static bool Lit((byte R, byte G, byte B) p) => p.R > 200 && p.G > 200 && p.B > 200;

    [AvaloniaFact]
    public void Lines_join_seamlessly_across_cells_and_rows()
    {
        var h = new Harness(600, 300);
        h.Feed("┌──────────┐\r\n│          │\r\n│          │\r\n│          │\r\n└──────────┘\r\n█████\r\n█████" + $"{E}[?25l");
        var frame = h.Capture();
        var c = h.View.CellSize;
        var scale = frame.PixelSize.Width / h.Window.Bounds.Width;

        // horizontal line through the middle of row 0, from the corner to the far corner
        var midY0 = (int)(0.5 * c.Height * scale);
        var hits = Enumerable.Range(0, (int)(c.Height * scale)).Where(y => Lit(Harness.Pixel(frame, (int)(5.5 * c.Width * scale), y))).ToArray();
        Assert.NotEmpty(hits);
        var lineY = hits[0];
        Assert.InRange(lineY, midY0 - 2, midY0 + 2);
        for (var x = (int)Math.Ceiling(0.5 * c.Width * scale) + 1; x < (int)(11.5 * c.Width * scale); x++)
        {
            Assert.True(Lit(Harness.Pixel(frame, x, lineY)), $"gap in ─ at x={x}");
        }

        // vertical line down column 0 through rows 0..4 (crosses four row boundaries)
        var hitsX = Enumerable.Range(0, (int)(c.Width * scale)).Where(x => Lit(Harness.Pixel(frame, x, (int)(2.5 * c.Height * scale)))).ToArray();
        Assert.NotEmpty(hitsX);
        var lineX = hitsX[0];
        for (var y = lineY + 1; y < (int)(4.5 * c.Height * scale); y++)
        {
            Assert.True(Lit(Harness.Pixel(frame, lineX, y)), $"gap in │ at y={y}");
        }

        // full blocks tile without seams, horizontally and vertically
        for (var y = (int)(5 * c.Height * scale); y < (int)(7 * c.Height * scale); y++)
        {
            for (var x = 0; x < (int)(5 * c.Width * scale) - 1; x++)
            {
                Assert.True(Lit(Harness.Pixel(frame, x, y)), $"seam in █ at {x},{y}");
            }
        }

        h.SaveZoom("80-box-join", 0, 0, 110, 140, 4);
    }

    [AvaloniaFact]
    public void Box_drawing_at_fractional_scaling()
    {
        var h = new Harness(600, 300);
        Avalonia.Headless.HeadlessWindowExtensions.SetRenderScaling(h.Window, 1.5);
        h.Pump();
        h.Feed(RenderingTests.Boxes);
        h.Save("81-boxes-scale150");
    }
}
