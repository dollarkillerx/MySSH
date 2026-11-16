using Avalonia.Data.Converters;
using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Desktop.Services;
using MySSH.Desktop.ViewModels;
using CarbonKind = MySSH.UI.Carbon.NotificationKind;

namespace MySSH.Desktop;

/// <summary>Keeps view models free of UI-library types.</summary>
public static class Converters
{
    public static readonly IValueConverter NotificationKind = new FuncValueConverter<ViewModels.NotificationKind, CarbonKind>(k => k switch
    {
        ViewModels.NotificationKind.Success => CarbonKind.Success,
        ViewModels.NotificationKind.Warning => CarbonKind.Warning,
        ViewModels.NotificationKind.Error => CarbonKind.Error,
        _ => CarbonKind.Info,
    });

    public static readonly IValueConverter AiPolicyText = new FuncValueConverter<AiPolicy, string>(p => Loc.Instance["aiPolicy." + p]);
    public static readonly IValueConverter AiPolicyHelp = new FuncValueConverter<AiPolicy, string>(p => Loc.Instance["aiPolicy." + p + ".help"]);
    public static readonly IValueConverter ThemeText = new FuncValueConverter<ThemePreference, string>(t => Loc.Instance[t switch
    {
        ThemePreference.Light => "settings.themeLight",
        ThemePreference.Dark => "settings.themeDark",
        _ => "settings.themeAuto",
    }]);
    public static readonly IValueConverter ProxyKindText = new FuncValueConverter<ProxyKind, string>(k => k == ProxyKind.Socks5 ? "SOCKS5" : "HTTP");
}
