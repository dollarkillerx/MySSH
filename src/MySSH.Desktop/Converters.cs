using Avalonia.Data.Converters;
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
}
