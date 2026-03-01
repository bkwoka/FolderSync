using System.Threading;
using System.Threading.Tasks;

namespace FolderSync.Services.Interfaces;

/// <summary>
/// Service responsible for direct REST API communication with Google Drive, 
/// bypassing the Rclone engine for tasks like permission management and account information retrieval.
/// </summary>
public interface IGoogleDriveApiService
{
    Task<(string Name, string Email)> GetGoogleUserInfoAsync(string tokenJson,
        CancellationToken cancellationToken = default);

    Task ShareFolderAsync(string rcloneRemote, string folderId, string targetEmail,
        CancellationToken cancellationToken = default);

    Task RevokePermissionAsync(string rcloneRemote, string folderId, string targetEmail,
        CancellationToken cancellationToken = default);

    Task TrashFileAsync(string rcloneRemote, string fileId, CancellationToken cancellationToken = default);

    Task<bool> VerifyFolderExistsAsync(string tokenJson, string folderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to automatically detect the folder ID for the "Google AI Studio" folder 
    /// by searching the drive and inspecting candidates for .prompt files.
    /// </summary>
    /// <param name="tokenJson">OAuth token JSON string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ID of the best matching folder, or null if not found.</returns>
    Task<string?> AutoDetectGoogleAiStudioFolderIdAsync(string tokenJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Securely deletes a folder or file only if it is owned by the current user.
    /// This prevents the accidental deletion of shared folders within the MESH network.
    /// </summary>
    /// <param name="rcloneRemote">The Rclone remote name to use for authentication.</param>
    /// <param name="folderId">The Google Drive ID of the folder to delete.</param>
    /// <param name="cancellationToken">Token for operation cancellation.</param>
    /// <returns>True if the folder was owned and successfully deleted; otherwise, false.</returns>
    Task<bool> DeleteFolderIfOwnedAsync(string rcloneRemote, string folderId, CancellationToken cancellationToken = default);
}