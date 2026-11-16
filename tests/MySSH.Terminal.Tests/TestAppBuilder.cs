using Avalonia;
using Avalonia.Headless;
using MySSH.Terminal.Tests;
using MySSH.UI.Carbon;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace MySSH.Terminal.Tests;

/// <summary>Headless app with the Carbon theme (tokens + IBM Plex Mono), rendered with real Skia pixels and fonts.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new CarbonTheme());
}
