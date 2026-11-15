using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;

namespace MySSH.UI.Carbon.Tests;

internal static class Render
{
    public static string WorkDir([CallerFilePath] string here = "")
    {
        var dir = Path.Combine(Path.GetDirectoryName(here)!, ".work", "screenshots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Hosts <paramref name="content"/> in a window of the given theme, sized to its content height.</summary>
    public static Window Show(Control content, ThemeVariant theme, double width = 1280, double? height = null)
    {
        var host = new Border { Child = content };
        host.Bind(Border.BackgroundProperty, host.GetResourceObservable("CarbonBackground"));
        var window = new Window
        {
            Width = width,
            Height = height ?? 800,
            RequestedThemeVariant = theme,
            Content = host,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        if (height is null)
        {
            content.Measure(new Size(width, double.PositiveInfinity));
            window.Height = Math.Clamp(Math.Ceiling(content.DesiredSize.Height), 100, 6000);
            Dispatcher.UIThread.RunJobs();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    public static string Save(Window window, string name)
    {
        // let brush / layout transitions (70-240ms) finish before capturing
        for (var i = 0; i < 30; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame rendered");
        var path = Path.Combine(WorkDir(), name + ".png");
        frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        return path;
    }
}
