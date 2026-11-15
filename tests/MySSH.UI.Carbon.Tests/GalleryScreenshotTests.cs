using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using MySSH.Carbon.Gallery;

namespace MySSH.UI.Carbon.Tests;

/// <summary>Renders every gallery page in Light (White) and Dark (Gray 100) to .work/screenshots/*.png.</summary>
public class GalleryScreenshotTests
{
    public static TheoryData<string, string> Pages()
    {
        var data = new TheoryData<string, string>();
        foreach (var page in GalleryPages.All)
        {
            data.Add(page.Id, "light");
            data.Add(page.Id, "dark");
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Pages))]
    public void Gallery_page_renders(string id, string variant)
    {
        LogCollector.Drain();
        var page = GalleryPages.All.Single(p => p.Id == id);
        var theme = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var window = Render.Show(page.Create(), theme);
        var path = Render.Save(window, $"{id}-{variant}");
        Assert.True(File.Exists(path));
        var logs = LogCollector.Drain();
        window.Close();
        Assert.True(logs.Count == 0, string.Join("\n", logs));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Main_window_renders(string variant)
    {
        LogCollector.Drain();
        var window = new MainWindow { RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var path = Render.Save(window, $"main-window-{variant}");
        Assert.True(File.Exists(path));
        var logs = LogCollector.Drain();
        window.Close();
        Assert.True(logs.Count == 0, string.Join("\n", logs));
    }
}
