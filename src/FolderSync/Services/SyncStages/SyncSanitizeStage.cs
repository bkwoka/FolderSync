using System;
using FolderSync.Helpers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services.SyncStages;

/// <summary>
/// Synchronization stage that identifies and resolves name collisions within a single Google Drive folder.
/// This prevents Rclone from failing during cross-account operations due to duplicate filenames.
/// </summary>
/// <param name="rclone">Service for executing Rclone commands.</param>
/// <param name="localizer">Service for localized string retrieval.</param>
public class SyncSanitizeStage(IRcloneService rclone, ITranslationService localizer) : ISyncSanitizeStage
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public async Task RunAsync(RemoteInfo remote, IProgress<SyncProgressEvent> uiLogger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string rootPath = $"{remote.RcloneRemote},root_folder_id={remote.FolderId}:";
        var allFiles = await rclone.ListItemsAsync(rootPath, false, cancellationToken);
        var conversationFiles = allFiles.Where(f => f.IsConversation).ToList();

        // Identify groups of files that share the exact same name
        var duplicates = conversationFiles.GroupBy(f => f.Name).Where(g => g.Count() > 1).ToList();

        if (duplicates.Count > 0)
        {
            int count = duplicates.Count;
            string remoteName = remote.FriendlyName;
            Logger.Warn("Sanity Check: Found {0} conversation name collisions in root of {1}. Initiating automatic resolution.", count, remoteName);

            var id = Guid.NewGuid();
            string template = localizer["Log_Stage0_FixingDuplicates"];
            string localizedMsg = string.Format(System.Globalization.CultureInfo.CurrentCulture, template, remoteName, count);
            uiLogger.Report(new SyncProgressEvent(id, localizedMsg, false, LogEntryType.Normal, 1));
            
            foreach (var group in duplicates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Keep the oldest version as the 'original', rename subsequent duplicates
                var orderedFiles = group.OrderBy(f => f.ModTime).ThenBy(f => f.Id).ToList();
                var original = orderedFiles.First();
                var copies = orderedFiles.Skip(1).ToList();

                Logger.Info("Preserving original conversation instance: '{FileName}' ({FileId})", original.Name, original.Id);

                foreach (var copy in copies)
                {
                    string ext = Path.GetExtension(copy.Name); 
                    string nameNoExt = Path.GetFileNameWithoutExtension(copy.Name);
                    string newName = $"{nameNoExt}_{copy.ModTime:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}{ext}";
                    
                    Logger.Info("Renaming duplicate conversation instance to avoid collision: '{OldName}' -> '{NewName}'", copy.Name, newName);
                    
                    try
                    {
                        // Use server-side 'moveto' for instant renaming without data transfer to minimize I/O overhead.
                        await rclone.ExecuteCommandAsync(new[] { "moveto", $"{rootPath}{copy.Name}", $"{rootPath}{newName}", "--drive-server-side-across-configs" }, null, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Fail-Fast: Do not suppress sanitization failures. 
                        // If file renaming fails, proceeding to the consolidation stage would be based on an 
                        // inconsistent remote state, potentially leading to data loss or further corruption.
                        Logger.Error(ex, "Critical failure while resolving collision for '{0}' on {1}.", copy.Name, remote.FriendlyName);
                        
                        string errMsg = string.Format(localizer["Error_SanitizeFailed"], copy.Name, remote.FriendlyName);
                        throw new InvalidOperationException(errMsg, ex);
                    }
                }
            }
            uiLogger.Report(new SyncProgressEvent(id, "", true));
        }
    }
}
