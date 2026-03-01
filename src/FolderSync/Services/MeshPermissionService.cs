using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services;

/// <summary>
/// Service responsible for managing folder sharing permissions between Google Drive accounts in the sync mesh.
/// </summary>
/// <param name="googleApi">The Google Drive API service.</param>
public class MeshPermissionService(IGoogleDriveApiService googleApi) : IMeshPermissionService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public async Task GrantMeshPermissionsAsync(RemoteInfo newRemote, List<RemoteInfo> existingRemotes,
        CancellationToken cancellationToken = default)
    {
        foreach (var existing in existingRemotes)
        {
            // Share new remote's folder with existing remote's account
            try
            {
                await googleApi.ShareFolderAsync(newRemote.RcloneRemote, newRemote.FolderId,
                    existing.Email ?? existing.RcloneRemote, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to share folder from {NewRemote} to {ExistingRemote}", newRemote.FriendlyName,
                    existing.FriendlyName);
            }

            // Share existing remote's folder with new remote's account
            try
            {
                await googleApi.ShareFolderAsync(existing.RcloneRemote, existing.FolderId,
                    newRemote.Email ?? newRemote.RcloneRemote, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to share folder from {ExistingRemote} to {NewRemote}", existing.FriendlyName,
                    newRemote.FriendlyName);
            }
        }
    }

    /// <inheritdoc />
    public async Task RevokeMeshPermissionsAsync(RemoteInfo targetToRemove, List<RemoteInfo> existingRemotes,
        CancellationToken cancellationToken = default)
    {
        Logger.Info("Starting Mesh Revocation for: {TargetName}", targetToRemove.FriendlyName);

        foreach (var other in existingRemotes)
        {
            // Individual try-catch blocks ensure that failures on one drive (e.g., 404 Not Found) don't stop the overall revocation process

            try
            {
                // Revoke target account's access to the other remote's folder
                await googleApi.RevokePermissionAsync(other.RcloneRemote, other.FolderId,
                    targetToRemove.Email ?? targetToRemove.RcloneRemote, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Could not revoke access for {TargetName} from folder owned by {OtherName}",
                    targetToRemove.FriendlyName, other.FriendlyName);
            }

            try
            {
                // Revoke the other account's access to the target remote's folder
                await googleApi.RevokePermissionAsync(targetToRemove.RcloneRemote, targetToRemove.FolderId,
                    other.Email ?? other.RcloneRemote, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Could not revoke access for {OtherName} from folder owned by {TargetName}",
                    other.FriendlyName, targetToRemove.FriendlyName);
            }
        }
    }
}