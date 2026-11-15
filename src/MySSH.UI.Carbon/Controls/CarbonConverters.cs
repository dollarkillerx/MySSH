using System.Globalization;
using Avalonia.Data.Converters;

namespace MySSH.UI.Carbon;

/// <summary>Small value converters used by the Carbon templates.</summary>
public static class CarbonConverters
{
    /// <summary>Multiplies a double by the (invariant-culture) parameter, e.g. 0.9 for "90% of".</summary>
    public static IValueConverter Fraction { get; } = new FuncValueConverter<double, object?, double>(
        (value, parameter) => value * (parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 1d,
        }));
}
