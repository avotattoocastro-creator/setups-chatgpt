using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Converters;

/// <summary>Maps <see cref="LogCategory"/> → badge background brush.</summary>
public sealed class CategoryToBadgeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (LogCategory)value switch
        {
            LogCategory.AI    => new SolidColorBrush(Windows.UI.Color.FromArgb(255,   0, 180, 180)), // teal
            LogCategory.DATA  => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  64, 128, 200)), // blue
            LogCategory.WARN  => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 160,   0)), // amber
            LogCategory.ERROR => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 210,  55,  55)), // red
            _                 => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  96, 128, 128)), // gray (INFO)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>Maps <see cref="LogCategory"/> → text foreground brush.</summary>
public sealed class CategoryToTextBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (LogCategory)value switch
        {
            LogCategory.AI    => new SolidColorBrush(Windows.UI.Color.FromArgb(255,   0, 210, 210)),
            LogCategory.DATA  => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 100, 170, 240)),
            LogCategory.WARN  => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 190,  40)),
            LogCategory.ERROR => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240,  90,  90)),
            _                 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 220, 220)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>Formats a <see cref="float"/> (0..1) as a percentage string, e.g. "73%".</summary>
public sealed class FloatToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is float f ? $"{f:P0}" : "0%";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>Inverts a <see cref="bool"/> value — used to enable controls only when not simulating.</summary>
public sealed class NotBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : false;
}

/// <summary>
/// Maps an AC connection status string to the appropriate foreground
/// <see cref="SolidColorBrush"/> for the status badge.
/// <list type="bullet">
///   <item>"Connected to AC" → green (#00E676)</item>
///   <item>"Disconnected - retrying..." → orange (#FFA040)</item>
///   <item>"AC not running" → red (#FF5050)</item>
///   <item>anything else (Simulation) → muted teal (#8AABAB)</item>
/// </list>
/// </summary>
public sealed class AcStatusToForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (value as string) switch
        {
            var status when status?.StartsWith("Connected",    StringComparison.OrdinalIgnoreCase) == true
                => new SolidColorBrush(Windows.UI.Color.FromArgb(255,   0, 230, 118)),  // green
            var status when status?.StartsWith("Disconnected", StringComparison.OrdinalIgnoreCase) == true
                => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 160,  64)),  // orange
            var status when status?.StartsWith("AC not",       StringComparison.OrdinalIgnoreCase) == true
                => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255,  80,  80)),  // red
            _   => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 138, 171, 171)),  // muted teal (Simulation)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>
/// Maps an AC connection status string to the appropriate background
/// <see cref="SolidColorBrush"/> for the status badge.
/// </summary>
public sealed class AcStatusToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (value as string) switch
        {
            var status when status?.StartsWith("Connected",    StringComparison.OrdinalIgnoreCase) == true
                => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  10,  30,  10)),  // dark green
            var status when status?.StartsWith("Disconnected", StringComparison.OrdinalIgnoreCase) == true
                => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  30,  21,   0)),  // dark orange
            var status when status?.StartsWith("AC not",       StringComparison.OrdinalIgnoreCase) == true
                => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  30,  10,  10)),  // dark red
            _   => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  24,  40,  40)),  // dark teal (Simulation)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>
/// Maps a <see cref="SetupDiffKind"/> to the appropriate foreground
/// <see cref="SolidColorBrush"/> for diff table rows.
/// </summary>
public sealed class DiffKindToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (SetupDiffKind)value switch
        {
            SetupDiffKind.Added   => new SolidColorBrush(Windows.UI.Color.FromArgb(255,   0, 214, 100)),  // green
            SetupDiffKind.Removed => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240,  80,  80)),  // red
            _                     => new SolidColorBrush(Windows.UI.Color.FromArgb(255,   0, 212, 180)),  // teal (Changed)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>
/// Maps a <c>DeltaSign</c> integer (+1, -1, 0) to the appropriate foreground
/// <see cref="SolidColorBrush"/> for +/- diff badges.
/// +1 → green, -1 → red, 0 → neutral teal.
/// </summary>
public sealed class DeltaSignToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is int sign
            ? sign switch
            {
                 1 => new SolidColorBrush(Windows.UI.Color.FromArgb(255,   0, 212, 100)), // green ▲
                -1 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240,  80,  80)), // red ▼
                _  => new SolidColorBrush(Windows.UI.Color.FromArgb(255,   0, 180, 180)), // teal ~
            }
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 138, 171, 171));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>
/// Maps a <c>DeltaSign</c> integer to the appropriate badge background brush.
/// </summary>
public sealed class DeltaSignToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is int sign
            ? sign switch
            {
                 1 => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  10,  35,  15)), // dark green
                -1 => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  35,  10,  10)), // dark red
                _  => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  10,  28,  28)), // dark teal
            }
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255,  20,  30,  30));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>Converts a <see cref="bool"/> to "Yes" / "No" string.</summary>
public sealed class BoolToYesNoConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? "Yes" : "No";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>
/// Returns <see cref="Microsoft.UI.Xaml.Visibility.Collapsed"/> when the value is
/// <see langword="null"/> or an empty string, otherwise <see cref="Microsoft.UI.Xaml.Visibility.Visible"/>.
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is null || (value is string s && string.IsNullOrEmpty(s)))
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>
/// Maps an AC connection status string to the appropriate border
/// <see cref="SolidColorBrush"/> for the status badge.
/// </summary>
public sealed class AcStatusToBorderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (value as string) switch
        {
            var status when status?.StartsWith("Connected",    StringComparison.OrdinalIgnoreCase) == true
                => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  30,  64,  48)),  // green-toned border
            var status when status?.StartsWith("Disconnected", StringComparison.OrdinalIgnoreCase) == true
                => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  64,  42,   0)),  // orange border
            var status when status?.StartsWith("AC not",       StringComparison.OrdinalIgnoreCase) == true
                => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  64,  20,  20)),  // red border
            _   => new SolidColorBrush(Windows.UI.Color.FromArgb(255,  46,  64,  64)),  // teal border
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
