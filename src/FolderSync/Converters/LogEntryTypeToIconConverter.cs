using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FolderSync.Models;

namespace FolderSync.Converters;

/// <summary>
/// Converts a LogEntryType to its corresponding vector icon.
/// Uses pre-parsed StreamGeometry instances to bypass Avalonia's implicit string-to-geometry
/// parsing during UI binding, significantly improving rendering performance.
/// </summary>
public class LogEntryTypeToIconConverter : IValueConverter
{
    private static readonly StreamGeometry WarningIcon = StreamGeometry.Parse("M12 5.99L19.53 19H4.47L12 5.99M12 2L1 21H23L12 2ZM13 16H11V18H13V16ZM13 10H11V15H13V10Z");
    private static readonly StreamGeometry SystemIcon = StreamGeometry.Parse("M20 2H4C2.9 2 2 2.9 2 4V7C2 7.8 2.5 8.5 3 8.8V20C3 21.1 3.9 22 5 22H19C20.1 22 21 21.1 21 20V8.8C21.5 8.5 22 7.8 22 7V4C22 2.9 21.1 2 20 2ZM19 20H5V9H19V20ZM20 7H4V4H20V7ZM9 12H15V14H9V12Z");
    private static readonly StreamGeometry InspectIcon = StreamGeometry.Parse("M15.5 14H14.71L14.43 13.73C15.41 12.59 16 11.11 16 9.5C16 5.91 13.09 3 9.5 3C5.91 3 3 5.91 3 9.5C3 13.09 5.91 16 9.5 16C11.11 16 12.59 15.41 13.73 14.43L14 14.71V15.5L19 20.49L20.49 19L15.5 14ZM9.5 14C7.01 14 5 11.99 5 9.5C5 7.01 7.01 5 9.5 5C11.99 5 14 7.01 14 9.5C14 11.99 11.99 14 9.5 14Z");
    private static readonly StreamGeometry NetworkIcon = StreamGeometry.Parse("M22 8L18 4V7H3V9H18V12L22 8ZM2 16L6 20V17H21V15H6V12L2 16Z");
    private static readonly StreamGeometry DownloadIcon = StreamGeometry.Parse("M19 9H15V3H9V9H5L12 16L19 9ZM5 18V20H19V18H5Z");
    private static readonly StreamGeometry UploadIcon = StreamGeometry.Parse("M9 16H15V10H19L12 3L5 10H9V16ZM5 18V20H19V18H5Z");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is LogEntryType type)
        {
            return type switch
            {
                LogEntryType.Warning => WarningIcon,
                LogEntryType.System => SystemIcon,
                LogEntryType.Inspect => InspectIcon,
                LogEntryType.Network => NetworkIcon,
                LogEntryType.Download => DownloadIcon,
                LogEntryType.Upload => UploadIcon,
                _ => null
            };
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
