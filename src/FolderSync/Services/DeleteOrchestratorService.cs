using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Exceptions;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services;

/// <summary>
/// Orchestrates the deletion of conversations and their associated attachments across multiple Google Drive accounts.
/// </summary>
public class DeleteOrchestratorService(IRcloneService rclone, IGoogleDriveApiService googleApi, IPromptMetadataParser metadataParser)
    : IDeleteOrchestratorService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Permanently deletes a conversation file and optionally moves its attachments to the trash across all configured remotes.
    /// </summary>
    public async Task DeleteConversationAsync(string fileName, bool deleteAttachments, RemoteInfo masterRemote,
        List<RemoteInfo> allRemotes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Logger.Info("Global Delete Initiated for file: '{FileName}'. DeleteAttachments: {DeleteAttachments}", fileName,
            deleteAttachments);

        var attachmentIds = new List<string>();
        
        // PHASE 1: Data Extraction
        // We MUST extract attachment IDs before destroying the conversation file, 
        // because the .prompt file acts as the only map to those attachments.
        if (deleteAttachments)
        {
            try
            {
                string jsonContent = await rclone.ReadFileContentAsync(masterRemote.RcloneRemote, masterRemote.FolderId,
                    fileName, cancellationToken);
                attachmentIds = metadataParser.ExtractAttachmentIds(jsonContent);
                Logger.Info("Extracted {0} attachment IDs for soft deletion.", attachmentIds.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Could not analyze file {FileName} for attachments. Proceeding with prompt deletion only.", fileName);
            }
        }

        var allMasterDirs = await rclone.ListItemsAsync($"{masterRemote.RcloneRemote}:", true, cancellationToken);
        var targetDirs = allMasterDirs.Where(d => d.Name == AppConstants.TargetFolderName).ToList();
        if (targetDirs.All(d => d.Id != masterRemote.FolderId))
        {
            targetDirs.Add(new RcloneItem(masterRemote.FolderId, AppConstants.TargetFolderName, DateTime.Now, true,
                null));
        }

        // PHASE 2: Permanent Annihilation of the .prompt file (Hard Delete)
        // Thread-safe collection to track failures across parallel tasks.
        var failedRemotes = new ConcurrentBag<string>();

        // Scatter-Gather parallel deletion from Master folders
        var masterDeleteTasks = targetDirs.Select(async dir =>
        {
            try
            {
                string targetPath = $"{masterRemote.RcloneRemote},root_folder_id={dir.Id}:{fileName}";
                // Permanently destroy the file by bypassing the trash bin
                await rclone.ExecuteCommandAsync(["deletefile", targetPath, "--drive-use-trash=false"], null,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                // IDEMPOTENCY: If the file is already gone, consider it a success.
                Logger.Debug("File {FileName} was already absent in master directory {DirId}.", fileName, dir.Id);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to permanently delete file from master directory {DirId}.", dir.Id);
                failedRemotes.Add(masterRemote.FriendlyName);
            }
        });
        await Task.WhenAll(masterDeleteTasks);

        // Scatter-Gather parallel propagation of Hard Delete to Slaves
        var slaveDeleteTasks = allRemotes.Where(r => r.FolderId != masterRemote.FolderId).Select(async slave =>
        {
            try
            {
                string targetPath = $"{slave.RcloneRemote},root_folder_id={slave.FolderId}:{fileName}";
                // Permanently destroy the file by bypassing the trash bin
                await rclone.ExecuteCommandAsync(["deletefile", targetPath, "--drive-use-trash=false"], null,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                // IDEMPOTENCY: File deleted earlier or never reached this drive.
                Logger.Debug("File {FileName} was already absent on slave {RemoteName}.", fileName, slave.FriendlyName);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to permanently delete file from slave drive {RemoteName}.", slave.FriendlyName);
                failedRemotes.Add(slave.FriendlyName);
            }
        });
        await Task.WhenAll(slaveDeleteTasks);

        // If ANY conversation file deletion failed, we throw a domain exception to inform the UI
        // about leftovers (ghost files) across the mesh.
        if (!failedRemotes.IsEmpty)
        {
            var uniqueFailures = failedRemotes.Distinct().ToList();
            throw new PartialDeletionException(uniqueFailures);
        }

        Logger.Info("Permanent deletion of .prompt file '{FileName}' completed across MESH.", fileName);

        // PHASE 3: Quarantine / Soft Delete of Attachments
        // Token-Roulette for attachments: We attempt deletion using each available account until one succeeds.
        // This is necessary because attachments may be owned by different accounts in the mesh.
        if (attachmentIds.Any())
        {
            foreach (var id in attachmentIds)
            {
                bool trashed = false;
                foreach (var remote in allRemotes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        // TrashFileAsync performs a SOFT DELETE (moves to Google Drive Trash)
                        await googleApi.TrashFileAsync(remote.RcloneRemote, id, cancellationToken);
                        trashed = true;
                        Logger.Info("Successfully moved attachment {0} to trash using account {1}", id,
                            remote.FriendlyName);
                        break;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Expected if this specific remote account doesn't own or have rights to the attachment
                    }
                    catch (System.Net.Http.HttpRequestException ex)
                    {
                        Logger.Warn(ex, "Network error trashing {0} via {1}.", id, remote.FriendlyName);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Trace(ex, "Failed to trash attachment {0} using account {1}", id, remote.FriendlyName);
                    }
                }

                if (!trashed)
                {
                    Logger.Warn("Could not move attachment {0} to trash. It might already be deleted or inaccessible.", id);
                    
                    // Register a virtual failure for attachments to notify the user via UI status updates.
                    failedRemotes.Add("Attachments / Załączniki"); 
                }
            }
        }

        // Final verification: Determine if any part of the deletion process (conversations or attachments) failed.
        if (!failedRemotes.IsEmpty)
        {
            var uniqueFailures = failedRemotes.Distinct().ToList();
            throw new PartialDeletionException(uniqueFailures);
        }

        Logger.Info("Global Delete procedure completed successfully for '{FileName}'.", fileName);
    }
}