using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace MySSH.Terminal.Tests;

/// <summary>Hosts a <see cref="TerminalView"/> in a headless window and drives the dispatcher synchronously.</summary>
internal sealed class Harness
{
    public const string E = "\u001b";

    public Harness(int width = 900, int height = 420, ThemeVariant? theme = null, double fontSize = 14, Action<TerminalView>? configure = null)
    {
        View = new TerminalView { FontSize = fontSize };
        configure?.Invoke(View);
        Window = new Window
        {
            Width = width,
            Height = height,
            RequestedThemeVariant = theme ?? ThemeVariant.Dark,
            Content = View,
        };
        Window.Show();
        Pump();
        View.Focus();
        Pump();
    }

    /// <summary>A harness whose window fits exactly <paramref name="cols"/>×<paramref name="rows"/> cells.</summary>
    public static Harness ForGrid(int cols, int rows, ThemeVariant? theme = null, double fontSize = 14)
    {
        var probe = new Harness(400, 200, theme, fontSize);
        var cell = probe.View.CellSize;
        probe.Window.Close();
        var h = new Harness((int)Math.Ceiling((cols * cell.Width) + 1), (int)Math.Ceiling(rows * cell.Height), theme, fontSize);
        if (h.View.Columns != cols || h.View.Rows != rows)
        {
            throw new InvalidOperationException($"grid is {h.View.Columns}x{h.View.Rows}, wanted {cols}x{rows}");
        }

        return h;
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds (background PTY readers post to it).</summary>
    public bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            Pump();
            if (condition())
            {
                return true;
            }

            Thread.Sleep(15);
        }

        Pump();
        return condition();
    }

    public TerminalView View { get; }
    public Window Window { get; }

    /// <summary>Runs posted jobs (pump, layout) and one render tick.</summary>
    public void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        View.ProcessPendingOutput();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public void Feed(string text)
    {
        View.Feed(Encoding.UTF8.GetBytes(text));
        Pump();
    }

    public WriteableBitmap Capture()
    {
        Pump();
        return Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame rendered");
    }

    public string Save(string name)
    {
        var frame = Capture();
        var path = Path.Combine(WorkDir(), name + ".png");
        frame.Save(path, PngBitmapEncoderOptions.Default);
        return path;
    }

    /// <summary>Saves a nearest-neighbour zoom of a region (physical pixels) for close visual inspection.</summary>
    public string SaveZoom(string name, int x, int y, int w, int h, int scale = 4)
    {
        var src = Capture();
        using var srcFb = src.Lock();
        var dst = new WriteableBitmap(new PixelSize(w * scale, h * scale), new Vector(96, 96), srcFb.Format, Avalonia.Platform.AlphaFormat.Premul);
        using (var dstFb = dst.Lock())
        {
            for (var dy = 0; dy < h * scale; dy++)
            {
                for (var dx = 0; dx < w * scale; dx++)
                {
                    var sx = Math.Min(src.PixelSize.Width - 1, x + (dx / scale));
                    var sy = Math.Min(src.PixelSize.Height - 1, y + (dy / scale));
                    var v = Marshal.ReadInt32(srcFb.Address, (sy * srcFb.RowBytes) + (sx * 4));
                    Marshal.WriteInt32(dstFb.Address, (dy * dstFb.RowBytes) + (dx * 4), v);
                }
            }
        }

        var path = Path.Combine(WorkDir(), name + ".png");
        dst.Save(path, PngBitmapEncoderOptions.Default);
        return path;
    }

    public string Screen() => View.GetVisibleText(View.Rows);

    public (int X, int Y) Cursor => (View.Engine.Buffer.X, View.Engine.Buffer.Y);

    public static string WorkDir([CallerFilePath] string here = "")
    {
        var dir = Path.Combine(Path.GetDirectoryName(here)!, ".work", "screenshots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string FixtureDir([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Fixtures");

    /// <summary>Reads a BGRA pixel from a captured frame (physical pixels).</summary>
    public static (byte R, byte G, byte B) Pixel(WriteableBitmap bmp, int x, int y)
    {
        using var fb = bmp.Lock();
        var o = (y * fb.RowBytes) + (x * 4);
        byte b0 = Marshal.ReadByte(fb.Address, o), b1 = Marshal.ReadByte(fb.Address, o + 1), b2 = Marshal.ReadByte(fb.Address, o + 2);
        return fb.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? (b0, b1, b2) : (b2, b1, b0);
    }
}
