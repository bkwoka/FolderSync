namespace FolderSync.Models;

/// <summary>
/// Defines the semantic type of a synchronization log entry,
/// allowing the UI to determine the appropriate icon and color without hardcoding visual assets in the ViewModel.
/// </summary>
public enum LogEntryType
{
    Normal,
    Warning,
    System,
    Inspect,
    Network,
    Download,
    Upload
}
