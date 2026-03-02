using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace AvoPerformanceSetupAI.Converters;

/// <summary>
/// Scales a <see cref="CornerRadius"/> or a uniform <see cref="double"/> by the current
/// <c>UiScale</c> application resource.
/// Usage: <c>CornerRadius="{Binding Source={StaticResource CornerRadiusBase}, Converter={StaticResource ScaleCr}}"</c>
/// </summary>
public sealed class ScaleCornerRadiusConverter : IValueConverter
{
    private static double CurrentScale =>
        Application.Current?.Resources.TryGetValue(ResourceKeys.UiScale, out var v) == true && v is double d
            ? d
            : 1.0;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double s = CurrentScale;
        if (value is CornerRadius cr)
            return new CornerRadius(cr.TopLeft * s, cr.TopRight * s, cr.BottomRight * s, cr.BottomLeft * s);
        if (value is double d)
            return new CornerRadius(d * s);
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
