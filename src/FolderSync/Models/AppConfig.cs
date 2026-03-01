using System.Collections.Generic;

namespace FolderSync.Models;

/// <summary>
/// Represents the application configuration including the list of remotes and the master remote identification.
/// </summary>
public class AppConfig
{
    /// <summary>
    /// Gets or sets the list of configured rclone remotes.
    /// </summary>
    public List<RemoteInfo> Remotes { get; set; } = [];

    /// <summary>
    /// Gets or sets the folder ID of the master remote.
    /// </summary>
    public string? MasterRemoteId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user has disabled the confirmation warning before renaming a file.
    /// </summary>
    public bool SkipRenameSyncWarning { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the user has disabled the confirmation warning before deleting a conversation.
    /// </summary>
    public bool SkipDeleteSyncWarning { get; set; } = false;

    /// <summary>
    /// Gets or sets the active user interface language (e.g., "en", "pl").
    /// </summary>
    public string Language { get; set; } = "en";
}