using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services;

/// <summary>
/// Orchestrates high-level operations related to remote Google Drive management, 
/// including authorization, configuration, and mesh permission handling.
/// </summary>
public class DriveOrchestratorService(
    IConfigService configService,
    IRcloneService rcloneService,
    IGoogleDriveApiService googleApi,
    IMeshPermissionService meshService) : IDriveOrchestratorService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Initiates the OAuth flow to authorize a new Google Drive account.
    /// </summary>
    public async Task<(string Token, string Name, string Email)> AuthorizeNewDriveAsync(
        CancellationToken cancellationToken)
    {
        string token = await rcloneService.AuthorizeGoogleDrive(cancellationToken);
        var userInfo = await googleApi.GetGoogleUserInfoAsync(token, cancellationToken);
        return (token, userInfo.Name, userInfo.Email);
    }

    /// <summary>
    /// Verifies if a specific folder ID exists and is accessible using the provided token.
    /// </summary>
    public Task<bool> VerifyFolderExistsAsync(string token, string folderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        return googleApi.VerifyFolderExistsAsync(token, folderId, cancellationToken);
    }

    /// <summary>
    /// Adds a new drive to the application configuration, configures Rclone, and updates mesh permissions.
    /// </summary>
    public async Task AddNewDriveAsync(string name, string email, string folderId, string token, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        await rcloneService.CreateRemote(email, token);

        try
        {
            var config = await configService.LoadConfigAsync();
            var newRemote = new RemoteInfo(name, email, folderId, email);
            var otherRemotes = config.Remotes
                .Where(r => !r.RcloneRemote.Equals(email, StringComparison.OrdinalIgnoreCase)).ToList();

            if (overwrite)
            {
                var existing = config.Remotes.FirstOrDefault(r =>
                    r.RcloneRemote.Equals(email, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    await meshService.RevokeMeshPermissionsAsync(existing, otherRemotes);
                    config.Remotes.Remove(existing);

                    // Remove old rclone config blocks.
                    // This prevents "Duplicate Sections" in the Rclone INI file, which could corrupt the engine state.
                    try
                    {
                        await rcloneService.DeleteRemoteAsync(existing.RcloneRemote);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Failed to clean old remote section from INI file.");
                    }
                }
            }

            config.Remotes.Add(newRemote);
            await meshService.GrantMeshPermissionsAsync(newRemote, otherRemotes);
            await configService.SaveConfigAsync(config);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to finalize adding drive. Rolling back Rclone configuration for {AccountEmail}", email);
            try
            {
                // Attempt to remove the partially configured remote to maintain Rclone INI integrity.
                await rcloneService.DeleteRemoteAsync(email);
            }
            catch (Exception rollbackEx)
            {
                // Compensating transaction failure: Log the error to ensure orphaned remotes 
                // in rclone.conf are traceable during debugging. We do not rethrow to avoid masking 
                // the primary exception 'ex'.
                Logger.Error(rollbackEx, "CRITICAL: Rollback failed! The remote '{0}' remains orphaned in rclone.conf.", email);
            }

            throw;
        }
    }

    /// <summary>
    /// Removes a drive from the configuration, revokes mesh permissions, and deletes the Rclone remote.
    /// </summary>
    public async Task DeleteDriveAsync(RemoteInfo targetToRemove)
    {
        ArgumentNullException.ThrowIfNull(targetToRemove);
        var config = await configService.LoadConfigAsync();
        var existingRemotes = config.Remotes.Where(r => r.RcloneRemote != targetToRemove.RcloneRemote).ToList();

        await meshService.RevokeMeshPermissionsAsync(targetToRemove, existingRemotes);
        await rcloneService.DeleteRemoteAsync(targetToRemove.RcloneRemote);

        config.Remotes.RemoveAll(r => r.RcloneRemote == targetToRemove.RcloneRemote);
        if (config.MasterRemoteId == targetToRemove.FolderId) config.MasterRemoteId = null;

        await configService.SaveConfigAsync(config);
    }

    /// <summary>
    /// Designates a specific drive as the master remote for the mesh.
    /// </summary>
    public async Task SetAsMasterAsync(RemoteInfo newMaster)
    {
        ArgumentNullException.ThrowIfNull(newMaster);
        var config = await configService.LoadConfigAsync();
        config.MasterRemoteId = newMaster.FolderId;
        await configService.SaveConfigAsync(config);
    }

    /// <summary>
    /// Retrieves the current state of drives, identifying any remotes that are missing from the Rclone configuration.
    /// </summary>
    public async Task<(AppConfig Config, List<RemoteInfo> CorruptedRemotes)> GetDrivesStateAsync()
    {
        var config = await configService.LoadConfigAsync();
        var actualRcloneRemotes = await rcloneService.GetConfiguredRemotesAsync();
        var corrupted = config.Remotes.Where(r => !actualRcloneRemotes.Contains(r.RcloneRemote)).ToList();
        return (config, corrupted);
    }

    /// <inheritdoc />
    public Task<string?> AutoDetectFolderIdAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return googleApi.AutoDetectGoogleAiStudioFolderIdAsync(token, cancellationToken);
    }
}