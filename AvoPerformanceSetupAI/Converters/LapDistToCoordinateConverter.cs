using System;
using Microsoft.UI.Xaml.Data;

namespace AvoPerformanceSetupAI.Converters;

public class LapDistToCoordinateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double pct && parameter is string maxStr && double.TryParse(maxStr, out var max))
        {
            return Math.Clamp(pct, 0, 1) * max;
        }
        return 0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}
