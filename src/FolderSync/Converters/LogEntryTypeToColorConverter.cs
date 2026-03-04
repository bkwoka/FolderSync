using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FolderSync.Models;

namespace FolderSync.Converters;

/// <summary>
/// Converts a LogEntryType to its corresponding UI color.
/// Uses cached SolidColorBrush instances to prevent memory allocation overhead
/// and reduce Garbage Collector pressure during rapid log updates.
/// </summary>
public class LogEntryTypeToColorConverter : IValueConverter
{
    private static readonly ISolidColorBrush WarningBrush = SolidColorBrush.Parse("#E05C5C");
    private static readonly ISolidColorBrush SystemBrush = SolidColorBrush.Parse("#8899A6");
    private static readonly ISolidColorBrush InspectBrush = SolidColorBrush.Parse("#0699BE");
    private static readonly ISolidColorBrush NetworkBrush = SolidColorBrush.Parse("#0699BE");
    private static readonly ISolidColorBrush DownloadBrush = SolidColorBrush.Parse("#6CCC3C");
    private static readonly ISolidColorBrush UploadBrush = SolidColorBrush.Parse("#6CCC3C");
    private static readonly ISolidColorBrush DefaultBrush = SolidColorBrush.Parse("#e4eaec");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is LogEntryType type)
        {
            return type switch
            {
                LogEntryType.Warning => WarningBrush,
                LogEntryType.System => SystemBrush,
                LogEntryType.Inspect => InspectBrush,
                LogEntryType.Network => NetworkBrush,
                LogEntryType.Download => DownloadBrush,
                LogEntryType.Upload => UploadBrush,
                _ => DefaultBrush
            };
        }
        return DefaultBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
