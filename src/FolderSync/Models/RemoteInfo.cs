using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FolderSync.Models;

public partial class RemoteInfo : ObservableObject
{
    private string _friendlyName = string.Empty;

    public string FriendlyName
    {
        get => _friendlyName;
        set => SetProperty(ref _friendlyName, value);
    }

    private string _rcloneRemote = string.Empty;

    public string RcloneRemote
    {
        get => _rcloneRemote;
        set => SetProperty(ref _rcloneRemote, value);
    }

    private string _folderId = string.Empty;

    public string FolderId
    {
        get => _folderId;
        set => SetProperty(ref _folderId, value);
    }

    private string? _email;

    public string? Email
    {
        get => _email;
        set => SetProperty(ref _email, value);
    }

    private bool _isMaster;

    /// <summary>
    /// Gets or sets a value indicating whether this remote is the master (primary source for synchronization).
    /// This property is used only for UI display and is not persisted in the configuration file.
    /// </summary>
    [JsonIgnore]
    public bool IsMaster
    {
        get => _isMaster;
        set => SetProperty(ref _isMaster, value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteInfo"/> class for JSON deserialization.
    /// </summary>
    public RemoteInfo()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteInfo"/> class with specified details.
    /// </summary>
    /// <param name="friendlyName">The user-friendly name for the remote.</param>
    /// <param name="rcloneRemote">The rclone remote name (e.g., "GoogleDrive:").</param>
    /// <param name="folderId">The ID of the folder within the remote to synchronize.</param>
    /// <param name="email">The email associated with the remote (optional).</param>
    public RemoteInfo(string friendlyName, string rcloneRemote, string folderId, string? email = null)
    {
        FriendlyName = friendlyName;
        RcloneRemote = rcloneRemote;
        FolderId = folderId;
        Email = email;
    }
}