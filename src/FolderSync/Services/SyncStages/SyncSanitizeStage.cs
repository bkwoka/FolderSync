using System;
using FolderSync.Helpers;
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
        string rootPath = $"{remote.RcloneRemote},root_folder_id={remote.FolderId}:";
        var allFiles = await rclone.ListItemsAsync(rootPath, false, cancellationToken);
        var conversationFiles = allFiles.Where(f => f.IsConversation).ToList();

        // Identify groups of files that share the exact same name
        var duplicates = conversationFiles.GroupBy(f => f.Name).Where(g => g.Count() > 1).ToList();

        if (duplicates.Any())
        {
            Logger.Warn("Sanity Check: Found {DuplicateCount} conversation name collisions in root of {RemoteName}. Initiating automatic resolution.", duplicates.Count, remote.FriendlyName);
            
            var id = Guid.NewGuid();
            uiLogger.Report(new SyncProgressEvent(id, $"    {string.Format(localizer["Log_Stage0_FixingDuplicates"], remote.FriendlyName, duplicates.Count)}", false));
            
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
                    
                    // Use server-side 'moveto' for instant renaming without data transfer
                    await rclone.ExecuteCommandAsync(new[] { "moveto", $"{rootPath}{copy.Name}", $"{rootPath}{newName}", "--drive-server-side-across-configs" }, null, cancellationToken);
                }
            }
            uiLogger.Report(new SyncProgressEvent(id, "", true));
        }
    }
}
