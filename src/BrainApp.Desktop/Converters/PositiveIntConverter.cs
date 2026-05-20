using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace BrainApp.Desktop.Converters;

public class PositiveIntConverter : IValueConverter
{
    public static readonly PositiveIntConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
