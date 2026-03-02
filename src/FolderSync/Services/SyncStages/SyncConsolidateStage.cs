using System;
using FolderSync.Helpers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services.SyncStages;

/// <summary>
/// Synchronization stage that consolidates files from orphaned or duplicate 'AIStudio_bak' folders 
/// back into the main target folder on the same drive.
/// </summary>
/// <param name="rclone">Service for executing Rclone commands.</param>
/// <param name="googleApi">Service for direct Google Drive API interactions.</param>
/// <param name="localizer">Service for localized string retrieval.</param>
public class SyncConsolidateStage(IRcloneService rclone, IGoogleDriveApiService googleApi, ITranslationService localizer) : ISyncConsolidateStage
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public async Task RunAsync(RemoteInfo remote, IProgress<SyncProgressEvent> uiLogger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Logger.Info("Consolidating drive: {FriendlyName}", remote.FriendlyName);

        var allDirs = await rclone.ListItemsAsync($"{remote.RcloneRemote}:", true, cancellationToken);
        var targetId = remote.FolderId;

        // Find all directories named 'AIStudio_bak' that are NOT our main target folder
        foreach (var dir in allDirs.Where(d => d.Name == AppConstants.TargetFolderName))
        {
            if (dir.Id == targetId) continue;
            cancellationToken.ThrowIfCancellationRequested();

            Logger.Warn("Found orphan target folder {DirId}. Beginning consolidation...", dir.Id);
            
            // Report consolidation progress to the user interface.
            var moveId = Guid.NewGuid();
            uiLogger.Report(new SyncProgressEvent(moveId, $"    {string.Format(localizer["Log_Stage1_MovingOrphan"], remote.FriendlyName, dir.Id)}", false));
            
            string sourcePath = $"{remote.RcloneRemote},root_folder_id={dir.Id}:";
            string destPath = $"{remote.RcloneRemote},root_folder_id={targetId}:";

            var filesToMove = await rclone.ListItemsAsync(sourcePath, false, cancellationToken);
            var existingFiles = await rclone.ListItemsAsync(destPath, false, cancellationToken);
            
            var filesToActuallyMove = filesToMove
                .Where(f => !f.Name.Equals(AppConstants.HistoryFileName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var existingFilesDict = new Dictionary<string, RcloneItem>();
            foreach (var f in existingFiles)
            {
                if (!existingFilesDict.TryGetValue(f.Name, out var existing) || f.ModTime > existing.ModTime)
                {
                    existingFilesDict[f.Name] = f;
                }
            }

            foreach (var file in filesToActuallyMove)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string finalName = file.Name;
                bool shouldMove = true;
                
                if (existingFilesDict.TryGetValue(file.Name, out var existingFile))
                {
                    if (file.IsConversation)
                    {
                        Logger.Info("Intra-drive name collision detected for conversation: '{FileName}'. Inspecting identity...", file.Name);
                       
                        var deepId = Guid.NewGuid();
                        uiLogger.Report(new SyncProgressEvent(deepId, $"    {string.Format(localizer["Log_Stage1_DeepInspect"], remote.FriendlyName, file.Name)}", false));
                        
                        string? orphanTime = await ExtractCreateTimeAsync(remote.RcloneRemote, dir.Id, file.Name, cancellationToken);
                        string? targetTime = await ExtractCreateTimeAsync(remote.RcloneRemote, targetId, existingFile.Name, cancellationToken);
                        
                        bool isSameIdentity = orphanTime is not null && orphanTime == targetTime;

                        if (isSameIdentity)
                        {
                            if (file.ModTime > existingFile.ModTime.AddSeconds(2))
                            {
                                Logger.Info("Identity match: Orphan version is newer. Overwriting main folder version.");
                                await rclone.ExecuteCommandAsync(new[] { "deletefile", $"{destPath}{existingFile.Name}", "--drive-use-trash=false" }, null, cancellationToken);
                                existingFilesDict[file.Name] = file;
                            }
                            else
                            {
                                Logger.Info("Identity match: Orphan version is older or identical. Skipping (will be purged).");
                                shouldMove = false; 
                            }
                        }
                        else
                        {
                            Logger.Warn("Identity mismatch for same-name file '{FileName}'. Renaming to avoid data loss.", file.Name);
                            string ext = Path.GetExtension(file.Name); 
                            string nameNoExt = Path.GetFileNameWithoutExtension(file.Name);
                            finalName = $"{nameNoExt}_{file.ModTime:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}{ext}";
                            existingFilesDict[finalName] = file;
                        }
                        
                        uiLogger.Report(new SyncProgressEvent(deepId, "", true));
                    }
                    else
                    {
                        string ext = Path.GetExtension(file.Name); 
                        string nameNoExt = Path.GetFileNameWithoutExtension(file.Name);
                        finalName = $"{nameNoExt}_{file.ModTime:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}{ext}";
                        existingFilesDict[finalName] = file; 
                    }
                }
                else 
                { 
                    existingFilesDict[finalName] = file; 
                }

                if (shouldMove)
                {
                    await rclone.ExecuteCommandAsync(new[] { "moveto", $"{sourcePath}{file.Name}", $"{destPath}{finalName}", "--drive-server-side-across-configs" }, null, cancellationToken);
                }
            }
            
            // REMEDIATION: Replace Rclone Purge with a direct API call to ensure safe deletion of owned resources.
            Logger.Info("Consolidation complete. Attempting to securely remove orphaned folder {0} via REST API.", dir.Id);
            
            bool deleted = await googleApi.DeleteFolderIfOwnedAsync(remote.RcloneRemote, dir.Id, cancellationToken);
            
            if (deleted)
            {
                Logger.Info("Orphaned folder {0} was successfully removed from the cloud.", dir.Id);
            }
            else
            {
                Logger.Warn("Orphaned folder {0} was not removed (ownership mismatch or API error).", dir.Id);
            }

            uiLogger.Report(new SyncProgressEvent(moveId, "", true));
        }
    }

    /// <summary>
    /// Extracts the original creation time from a conversation JSON file to assist in identity matching.
    /// </summary>
    private async Task<string?> ExtractCreateTimeAsync(string remoteName, string folderId, string fileName, CancellationToken cancellationToken)
    {
        try
        {
            string jsonContent = await rclone.ReadFileContentAsync(remoteName, folderId, fileName, cancellationToken);
            if (string.IsNullOrWhiteSpace(jsonContent)) return null;
            
            using var doc = JsonDocument.Parse(jsonContent);
            if (doc.RootElement.TryGetProperty("chunkedPrompt", out var chunkedPrompt) && 
                chunkedPrompt.TryGetProperty("chunks", out var chunks) && 
                chunks.ValueKind == JsonValueKind.Array)
            {
                foreach (var chunk in chunks.EnumerateArray())
                {
                    if (chunk.TryGetProperty("createTime", out var timeElement)) 
                    {
                        return timeElement.GetString();
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) 
        { 
            Logger.Warn(ex, "Failed to extract createTime for {FileName}.", fileName); 
        }
        return null;
    }
}
