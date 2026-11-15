using Avalonia;
using Avalonia.Headless;
using MySSH.UI.Carbon.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace MySSH.UI.Carbon.Tests;

/// <summary>Headless app = the gallery App (CarbonTheme) rendered with real Skia pixels and fonts.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        LogCollector.Install();
        return AppBuilder.Configure<MySSH.Carbon.Gallery.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
