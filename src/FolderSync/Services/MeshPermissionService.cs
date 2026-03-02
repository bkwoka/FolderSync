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
    public async Task GrantMeshPermissionsAsync(RemoteInfo newRemote, List<RemoteInfo> existingRemotes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Logger.Info("Initiating MESH permission distribution for account: {0}", newRemote.FriendlyName);
        
        // Compensation registry for the Saga pattern mechanism
        var rollbackActions = new List<Func<Task>>();

        try
        {
            foreach (var existing in existingRemotes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // PHASE 1: Share the new drive's folder with the existing account
                string existingTargetEmail = existing.Email ?? existing.RcloneRemote;
                await googleApi.ShareFolderAsync(newRemote.RcloneRemote, newRemote.FolderId, existingTargetEmail, cancellationToken);
                
                // Register compensation action in case of failure in subsequent steps
                rollbackActions.Add(() => googleApi.RevokePermissionAsync(newRemote.RcloneRemote, newRemote.FolderId, existingTargetEmail, CancellationToken.None));

                // PHASE 2: Share the existing drive's folder with the new account
                string newTargetEmail = newRemote.Email ?? newRemote.RcloneRemote;
                await googleApi.ShareFolderAsync(existing.RcloneRemote, existing.FolderId, newTargetEmail, cancellationToken);
                
                // Register compensation action
                rollbackActions.Add(() => googleApi.RevokePermissionAsync(existing.RcloneRemote, existing.FolderId, newTargetEmail, cancellationToken));
            }
            
            Logger.Info("Successfully distributed all MESH permissions for: {0}", newRemote.FriendlyName);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Critical error during MESH permission granting. Rolling back {0} successful share operations.", rollbackActions.Count);

            // Compensation mechanism: revoke permissions that were successfully granted before the error occurred.
            // This prevents an architectural "Split-Brain" scenario where a drive has partial access to the mesh.
            foreach (var rollback in rollbackActions)
            {
                try
                {
                    await rollback();
                }
                catch (Exception rollbackEx)
                {
                    // Fail-safe for the rollback loop: one failed compensation should not stop others.
                    Logger.Warn(rollbackEx, "Warning: Failed to revoke permission during the rollback procedure.");
                }
            }

            // Propagate the exception to be handled by higher-level services (e.g., DriveOrchestratorService),
            // which may need to remove the faulty drive configuration from the Rclone engine.
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RevokeMeshPermissionsAsync(RemoteInfo targetToRemove, List<RemoteInfo> existingRemotes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Logger.Info("Starting Mesh Revocation for: {TargetName}", targetToRemove.FriendlyName);

        foreach (var other in existingRemotes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Individual try-catch blocks ensure that failures on one drive (e.g., 404 Not Found) don't stop the overall revocation process

            try
            {
                // Revoke target account's access to the other remote's folder
                await googleApi.RevokePermissionAsync(other.RcloneRemote, other.FolderId,
                    targetToRemove.Email ?? targetToRemove.RcloneRemote, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Could not revoke access for {OtherName} from folder owned by {TargetName}",
                    other.FriendlyName, targetToRemove.FriendlyName);
            }
        }
    }
}