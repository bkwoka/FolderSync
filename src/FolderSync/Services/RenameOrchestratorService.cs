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

public class RenameOrchestratorService(IRcloneService rclone, ITranslationService localizer)
    : IRenameOrchestratorService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public async Task RenameConversationAsync(string oldFullName, string newFullName, RemoteInfo masterRemote,
        List<RemoteInfo> allRemotes, CancellationToken cancellationToken = default)
    {
        Logger.Info("Global Rename Initiated: '{0}' -> '{1}'", oldFullName, newFullName);

        // 1. Sequential execution on the Master drive (Guarantees consistency and zero collisions).
        var allMasterDirs = await rclone.ListItemsAsync($"{masterRemote.RcloneRemote}:", true, cancellationToken);
        var targetDirs = allMasterDirs.Where(d => d.Name == AppConstants.TargetFolderName).ToList();

        if (!targetDirs.Any(d => d.Id == masterRemote.FolderId))
        {
            targetDirs.Add(new RcloneItem(masterRemote.FolderId, AppConstants.TargetFolderName, DateTime.UtcNow, true,
                null));
        }

        var dirsContainingOldFile = new List<string>();

        Logger.Info("Scanning {0} folders on Master drive for collisions...", targetDirs.Count);
        foreach (var dir in targetDirs)
        {
            var files = await rclone.ListItemsAsync($"{masterRemote.RcloneRemote},root_folder_id={dir.Id}:", false,
                cancellationToken);

            if (files.Any(f => f.Name.Equals(newFullName, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.Warn("Collision detected on Master for name: {0}", newFullName);
                throw new InvalidOperationException(string.Format(localizer["Error_RenameCollision"], newFullName));
            }

            if (files.Any(f => f.Name.Equals(oldFullName, StringComparison.OrdinalIgnoreCase)))
            {
                dirsContainingOldFile.Add(dir.Id);
            }
        }

        foreach (var dirId in dirsContainingOldFile)
        {
            string src = $"{masterRemote.RcloneRemote},root_folder_id={dirId}:{oldFullName}";
            string dst = $"{masterRemote.RcloneRemote},root_folder_id={dirId}:{newFullName}";
            Logger.Info("Renaming on Master in folder {0}...", dirId);
            await rclone.ExecuteCommandAsync(new[] { "moveto", src, dst, "--drive-server-side-across-configs" }, null,
                cancellationToken);
        }

        // 2. Parallel execution on Slave drives (Scatter-Gather pattern with Jitter).
        var slaves = allRemotes.Where(r => r.FolderId != masterRemote.FolderId).ToList();
        var failedRemotes = new ConcurrentBag<string>();

        var renameTasks = slaves.Select(async (slave, index) =>
        {
            // Intelligent Jitter: prevents firing multiple rclone.exe processes simultaneously.
            await Task.Delay(index * 200, cancellationToken);

            try
            {
                string src = $"{slave.RcloneRemote},root_folder_id={slave.FolderId}:{oldFullName}";
                string dst = $"{slave.RcloneRemote},root_folder_id={slave.FolderId}:{newFullName}";
                Logger.Debug("Blind rename attempt on slave: {0}", slave.FriendlyName);

                await rclone.ExecuteCommandAsync(new[] { "moveto", src, dst, "--drive-server-side-across-configs" },
                    null, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                // IDEMPOTENCY: Expected behavior if the file is not present on the Slave drive yet.
                Logger.Debug("File {0} did not exist in root folder of {1}. Ignoring.", oldFullName,
                    slave.FriendlyName);
            }
            catch (Exception ex)
            {
                // Real failure (Network, Timeout, 403 Forbidden). We must report this to avoid Split-Brain silently.
                Logger.Error(ex, "Failed to rename file on slave {0}.", slave.FriendlyName);
                failedRemotes.Add(slave.FriendlyName);
            }
        });

        // Wait for all Slave drives to complete their parallel execution.
        await Task.WhenAll(renameTasks);

        if (!failedRemotes.IsEmpty)
        {
            throw new PartialRenameException(failedRemotes.Distinct().ToList());
        }

        Logger.Info("Global Rename Completed Successfully.");
    }
}