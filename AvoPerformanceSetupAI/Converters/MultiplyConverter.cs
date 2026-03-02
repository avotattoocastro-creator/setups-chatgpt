using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace AvoPerformanceSetupAI.Converters;

/// <summary>
/// Multiplies a base <see cref="double"/> (e.g. <c>FontBaseSmall</c>) by the current
/// <c>UiScale</c> application resource so every scaled font-size is kept in one place.
/// Usage in XAML:
///   <c>FontSize="{Binding Source={StaticResource FontBaseSmall}, Converter={StaticResource Mul}}"</c>
/// </summary>
public sealed class MultiplyConverter : IValueConverter
{
    private static double CurrentScale =>
        Application.Current?.Resources.TryGetValue(ResourceKeys.UiScale, out var v) == true && v is double d
            ? d
            : 1.0;

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is double d ? d * CurrentScale : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
