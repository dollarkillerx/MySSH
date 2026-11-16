using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace MySSH.UI.Carbon;

/// <summary>Small value converters used by the Carbon templates.</summary>
public static class CarbonConverters
{
    /// <summary>
    /// Content → tooltip text. A control can only have one visual parent, so a tooltip must never show the content
    /// control itself (that threw "already has a visual parent" when the rail tooltip opened over a TextBlock label).
    /// </summary>
    public static IValueConverter ContentText { get; } = new FuncValueConverter<object?, string?>(content => content switch
    {
        string s => s,
        TextBlock tb => tb.Text,
        ContentControl { Content: string s } => s,
        _ => null,
    });

    /// <summary>Multiplies a double by the (invariant-culture) parameter, e.g. 0.9 for "90% of".</summary>
    public static IValueConverter Fraction { get; } = new FuncValueConverter<double, object?, double>(
        (value, parameter) => value * (parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 1d,
        }));
}
