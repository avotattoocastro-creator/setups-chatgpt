using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace AvoPerformanceSetupAI.Converters;

/// <summary>
/// Scales a <see cref="Thickness"/> or a uniform <see cref="double"/> by the current
/// <c>UiScale</c> application resource.
/// Usage: <c>Padding="{Binding Source={StaticResource CardPaddingBase}, Converter={StaticResource ScaleTh}}"</c>
/// </summary>
public sealed class ScaleThicknessConverter : IValueConverter
{
    private static double CurrentScale =>
        Application.Current?.Resources.TryGetValue(ResourceKeys.UiScale, out var v) == true && v is double d
            ? d
            : 1.0;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double s = CurrentScale;
        if (value is Thickness t)
            return new Thickness(t.Left * s, t.Top * s, t.Right * s, t.Bottom * s);
        if (value is double d)
            return new Thickness(d * s);
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
