using System;
using System.Globalization;
using Avalonia.Data.Converters;
using FolderSync.Models;

namespace FolderSync.Converters;

public class LogEntryTypeToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is LogEntryType type)
        {
            return type != LogEntryType.Normal;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
