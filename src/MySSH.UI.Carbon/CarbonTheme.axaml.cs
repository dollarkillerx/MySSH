using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace MySSH.UI.Carbon;

/// <summary>
/// The Carbon theme: tokens, IBM Plex fonts, typography classes, icons and control themes.
/// Add it to <c>Application.Styles</c> instead of <c>SimpleTheme</c>/<c>FluentTheme</c>.
/// </summary>
public class CarbonTheme : Styles
{
    /// <summary>Creates the theme (XAML: <c>&lt;carbon:CarbonTheme /&gt;</c>).</summary>
    public CarbonTheme(IServiceProvider? serviceProvider = null)
    {
        AvaloniaXamlLoader.Load(serviceProvider, this);
    }
}
