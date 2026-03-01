using System;
using FolderSync.Helpers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services.SyncStages;

/// <summary>
/// Handles the cross-account synchronization phase, including aggregation to the master drive 
/// and distribution to slave drives.
/// </summary>
public class SyncCrossAccountStage(IRcloneService rclone, ITranslationService localizer) : ISyncCrossAccountStage
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public async Task RunAsync(List<RemoteInfo> remotes, RemoteInfo master, IProgress<SyncProgressEvent> uiLogger,
        Action advanceProgress, CancellationToken cancellationToken)
    {
        var headerId = Guid.NewGuid();
        uiLogger.Report(new SyncProgressEvent(headerId, $"\n{localizer["Log_Header_Sync"]}\n", false));
        uiLogger.Report(new SyncProgressEvent(headerId, "", true));
        string masterPath = $"{master.RcloneRemote},root_folder_id={master.FolderId}:";

        var aggId = Guid.NewGuid();
        uiLogger.Report(new SyncProgressEvent(aggId, $"  ⇄ {localizer["Log_Stage2_Aggregate"]}", false));
        uiLogger.Report(new SyncProgressEvent(aggId, "", true));
        var masterFiles = await rclone.ListItemsAsync(masterPath, false, cancellationToken);

        // Protection against external duplicates on Google Drive.
        // If multiple files with the same name exist, we select the newest one to ensure data integrity.
        var masterConversations = masterFiles
            .Where(f => f.IsConversation)
            .GroupBy(f => f.Name)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ModTime).First());

        // PHASE 2A: AGGREGATION (SEQUENTIAL)
        // Secondary drives upload data to the master drive one by one. This serial execution prevents 
        // race conditions on Google Drive servers and avoids duplicate entries during cross-account transfers.
        foreach (var remote in remotes.Where(r => r.FolderId != master.FolderId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string sourcePath = $"{remote.RcloneRemote},root_folder_id={remote.FolderId}:";
            var sourceFiles = await rclone.ListItemsAsync(sourcePath, false, cancellationToken);

            var toCopy = sourceFiles
                .Where(f => f.IsConversation)
                .Where(srcFile => !masterConversations.TryGetValue(srcFile.Name, out var mFile) ||
                                  srcFile.ModTime > mFile.ModTime.AddSeconds(2))
                .Select(f => f.Name)
                .ToList();

            if (toCopy.Any())
            {
                var downloadTaskId = Guid.NewGuid();
                uiLogger.Report(new SyncProgressEvent(downloadTaskId,
                    $"    ⬇ {string.Format(localizer["Log_Stage2_Download"], toCopy.Count, remote.FriendlyName)}",
                    false));
                await rclone.ExecuteCommandAsync(
                    new[] { "copy", sourcePath, masterPath, "--files-from-raw", "-", 
                        "--drive-server-side-across-configs", "--drive-use-trash=false" },
                    toCopy, cancellationToken, TimeSpan.FromMinutes(20));
                uiLogger.Report(new SyncProgressEvent(downloadTaskId, "", true));
            }

            advanceProgress();
        }

        var distId = Guid.NewGuid();
        uiLogger.Report(new SyncProgressEvent(distId, $"\n  ⇄ {localizer["Log_Stage2_Distribute"]}", false));
        uiLogger.Report(new SyncProgressEvent(distId, "", true));

        // Refresh the master file list after full aggregation to include all newly uploaded conversations.
        masterFiles = await rclone.ListItemsAsync(masterPath, false, cancellationToken);
        masterConversations = masterFiles
            .Where(f => f.IsConversation)
            .GroupBy(f => f.Name)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ModTime).First());

        // PHASE 2B: DISTRIBUTION (PARALLEL)
        // After aggregation, the master drive distributes the updated content to all secondary drives.
        // Since each destination drive is independent, we can execute these transfers in parallel using Task.WhenAll.
        var distributeTasks = remotes.Where(r => r.FolderId != master.FolderId).Select(async remote =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            string destPath = $"{remote.RcloneRemote},root_folder_id={remote.FolderId}:";
            var destFiles = await rclone.ListItemsAsync(destPath, false, cancellationToken);
            var destConversations = destFiles
                .Where(f => f.IsConversation)
                .GroupBy(f => f.Name)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ModTime).First());

            var toCopy = masterConversations.Values
                .Where(mFile => !destConversations.TryGetValue(mFile.Name, out var dFile) ||
                                mFile.ModTime > dFile.ModTime.AddSeconds(2))
                .Select(f => f.Name)
                .ToList();

            if (toCopy.Any())
            {
                var uploadTaskId = Guid.NewGuid();
                uiLogger.Report(new SyncProgressEvent(uploadTaskId,
                    $"    ⬆ {string.Format(localizer["Log_Stage2_Upload"], toCopy.Count, remote.FriendlyName)}",
                    false));
                await rclone.ExecuteCommandAsync(
                    new[] { "copy", masterPath, destPath, "--files-from-raw", "-", 
                        "--drive-server-side-across-configs", "--drive-use-trash=false" },
                    toCopy, cancellationToken, TimeSpan.FromMinutes(20));
                uiLogger.Report(new SyncProgressEvent(uploadTaskId, "", true));
            }

            advanceProgress();
        });

        await Task.WhenAll(distributeTasks);
    }
}