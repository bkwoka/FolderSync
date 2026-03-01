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
}