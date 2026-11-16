using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace MySSH.Terminal.Tests;

/// <summary>
/// Frame cost of the row-cached renderer on a 160×50 grid (design §4.3 target: full dense repaint ≤ 4 ms;
/// P0 spike: 12–16 ms at 178×56). "record" = UI-thread Render() work of all row visuals + cursor overlay,
/// the number comparable to the spike's Render() time; "whole frame" adds composition and Skia rasterisation.
/// Ceilings are generous so CI noise does not flake; the real numbers are printed.
/// </summary>
[Collection(nameof(SerialBenchmarks))]
public class RenderBenchmarkTests(ITestOutputHelper output)
{
    private const string E = Harness.E;
    private const int Cols = 160, RowsCount = 50, Frames = 100;

    private static readonly string[] Words =
        ["GET", "/api/v1/users", "200", "OK", "错误", "連携", "nginx", "upstream", "timeout", "接続", "完了", "│", "─", "latency=23ms", "id=4f2a"];

    private static Harness Grid()
    {
        var probe = new Harness(400, 200);
        var cell = probe.View.CellSize;
        probe.Window.Close();
        var h = new Harness((int)Math.Ceiling((Cols * cell.Width) + 1), (int)Math.Ceiling(RowsCount * cell.Height));
        Assert.Equal(Cols, h.View.Columns);
        Assert.Equal(RowsCount, h.View.Rows);
        return h;
    }

    private static string DenseScreen(Random rnd)
    {
        var sb = new StringBuilder($"{E}[H");
        for (var l = 0; l < RowsCount; l++)
        {
            var width = 0;
            while (true)
            {
                var w = Words[rnd.Next(Words.Length)];
                var cells = w.Sum(c => c > 0x2E80 ? 2 : 1) + 1;
                if (width + cells > Cols)
                {
                    break;
                }

                sb.Append(CultureInfo.InvariantCulture, $"{E}[38;5;{rnd.Next(256)}m");
                if (rnd.Next(6) == 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{E}[48;5;{rnd.Next(256)}m");
                }

                sb.Append(w).Append(' ');
                sb.Append(CultureInfo.InvariantCulture, $"{E}[0m");
                width += cells;
            }

            sb.Append(CultureInfo.InvariantCulture, $"{E}[K");
            if (l < RowsCount - 1)
            {
                sb.Append("\r\n");
            }
        }

        return sb.ToString();
    }

    private (double P50, double P95, double Max, double RowsPerFrame, double FrameP50, double BuiltPerFrame) Measure(Harness h, Func<int, string> frame)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var record = new List<double>();
        var full = new List<double>();
        long rows = 0, built = 0, allocated = 0;
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        double drawMs = 0;
        for (var i = 0; i < Frames + 5; i++)
        {
            h.View.Feed(Encoding.UTF8.GetBytes(frame(i))); // parse + row assignment (not timed: not rendering)
            h.View.Stats.Reset();
            var alloc0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(); // UI render pass + composition + Skia raster of the frame
            Dispatcher.UIThread.RunJobs();
            sw.Stop();
            if (i >= 5)
            {
                allocated += GC.GetAllocatedBytesForCurrentThread() - alloc0;
            }

            if (i < 5)
            {
                continue; // warm-up: glyph caches, JIT
            }

            record.Add((h.View.Stats.RowTime + h.View.Stats.OverlayTime).TotalMilliseconds);
            drawMs += h.View.Stats.DrawTime.TotalMilliseconds;
            full.Add(sw.Elapsed.TotalMilliseconds);
            rows += h.View.Stats.RowsRendered;
            built += h.View.Stats.RowsBuilt;
        }

        output.WriteLine($"  (of which replay/draw avg {drawMs / Frames:F2}ms; render alloc {allocated / Frames / 1024}KB/frame; GCs gen0={GC.CollectionCount(0) - gc0} gen1={GC.CollectionCount(1) - gc1} gen2={GC.CollectionCount(2) - gc2})");
        record.Sort();
        full.Sort();
        return (record[record.Count / 2], record[(int)(record.Count * 0.95)], record[^1], (double)rows / Frames, full[full.Count / 2], (double)built / Frames);
    }

    [AvaloniaFact]
    public void Dense_full_screen_repaint_160x50()
    {
        var h = Grid();
        var rnd = new Random(42);
        var r = Measure(h, _ => DenseScreen(rnd));
        h.Save("10-bench-dense");
        output.WriteLine($"dense full repaint {Cols}x{RowsCount}: record p50={r.P50:F2}ms p95={r.P95:F2}ms max={r.Max:F2}ms, rows rendered/frame={r.RowsPerFrame:F1}, rebuilt/frame={r.BuiltPerFrame:F1}, whole frame incl. compositor/Skia p50={r.FrameP50:F2}ms");
        Assert.True(r.RowsPerFrame >= RowsCount - 1, $"every row changed, expected ~{RowsCount} rebuilt, got {r.RowsPerFrame}");
        Assert.True(r.P95 < 8, $"p95 {r.P95:F2}ms exceeds the 8ms ceiling (target 4ms)");
    }

    [AvaloniaFact]
    public void Single_cell_change_rebuilds_one_row()
    {
        var h = Grid();
        h.Feed(DenseScreen(new Random(7)));
        var r = Measure(h, i => $"{E}[25;{(i % 100) + 1}H{(char)('a' + (i % 26))}");
        output.WriteLine($"one cell per frame: record p50={r.P50:F3}ms p95={r.P95:F3}ms, rows rendered/frame={r.RowsPerFrame:F2}, rebuilt/frame={r.BuiltPerFrame:F2}, whole frame incl. compositor/Skia p50={r.FrameP50:F2}ms");
        Assert.True(r.BuiltPerFrame <= 1.01, $"only the touched row should be rebuilt, got {r.BuiltPerFrame:F2} rows/frame");
        Assert.True(r.RowsPerFrame <= 1.01, $"untouched rows must not even be re-rendered, got {r.RowsPerFrame:F2} rows/frame");
        Assert.True(r.P95 < 2, $"p95 {r.P95:F3}ms");
    }

    [AvaloniaFact]
    public void Scrolling_reuses_row_caches()
    {
        var h = Grid();
        h.Feed(DenseScreen(new Random(3)));
        h.Feed($"{E}[{RowsCount};1H");
        var rnd = new Random(5);
        var r = Measure(h, _ =>
        {
            // three new coloured lines scroll in at the bottom each frame
            var sb = new StringBuilder();
            for (var l = 0; l < 3; l++)
            {
                sb.Append("\r\n");
                for (var w = 0; w < 12; w++)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{E}[38;5;{rnd.Next(256)}m{Words[rnd.Next(Words.Length)]} ");
                }

                sb.Append(CultureInfo.InvariantCulture, $"{E}[0m");
            }

            return sb.ToString();
        });
        output.WriteLine($"scroll 3 lines/frame: record p50={r.P50:F2}ms p95={r.P95:F2}ms, rows rendered/frame={r.RowsPerFrame:F1}, rebuilt/frame={r.BuiltPerFrame:F1}, whole frame incl. compositor/Skia p50={r.FrameP50:F2}ms");
        // moved rows are replayed from their recorded drawing (a transform change re-renders a visual in Avalonia),
        // only the lines that scrolled in are rebuilt
        Assert.True(r.BuiltPerFrame <= 3.5, $"scrolled rows must move, not rebuild: {r.BuiltPerFrame:F1} rows/frame");
        Assert.True(r.P95 < 3, $"p95 {r.P95:F2}ms");
    }
}

/// <summary>Benchmarks run alone: other test classes would otherwise run in parallel and add noise.</summary>
[CollectionDefinition(nameof(SerialBenchmarks), DisableParallelization = true)]
public sealed class SerialBenchmarks;
