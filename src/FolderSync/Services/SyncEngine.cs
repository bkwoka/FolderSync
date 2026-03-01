using System;
using FolderSync.Helpers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services;

/// <summary>
/// The core orchestration engine that manages the different stages of the synchronization process.
/// </summary>
public class SyncEngine(
    ISyncSanitizeStage sanitizeStage,
    ISyncConsolidateStage consolidateStage,
    ISyncCrossAccountStage crossAccountStage,
    ITranslationService localizer) : ISyncEngine
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Executes a full synchronization cycle across all configured remotes.
    /// </summary>
    public async Task RunFullSync(List<RemoteInfo> remotes, RemoteInfo master, IProgress<SyncProgressEvent> uiLogger,
        IProgress<double> progressUpdater, CancellationToken cancellationToken = default)
    {
        if (remotes == null || remotes.Count < 2)
        {
            var errId = Guid.NewGuid();
            uiLogger.Report(new SyncProgressEvent(errId, $"⚠️ {localizer["Error_NeedTwoDrives"]}", false));
            uiLogger.Report(new SyncProgressEvent(errId, "", true));
            progressUpdater.Report(100);
            return;
        }

        if (!remotes.Contains(master))
            throw new ArgumentException("Master remote must be present in the remotes list.");

        Logger.Info("=== START SESSION: ORCHESTRATED ENGINE ===");

        try
        {
            int totalSteps = (remotes.Count * 2) + ((remotes.Count - 1) * 2);
            int currentStep = 0;

            void AdvanceProgress()
            {
                int step = Interlocked.Increment(ref currentStep);
                progressUpdater.Report(Math.Min(100.0, (double)step / totalSteps * 100));
            }

            progressUpdater.Report(0);

            // Phase 1: Preparation (Sanitization and Consolidation)
            var headId = Guid.NewGuid();
            uiLogger.Report(new SyncProgressEvent(headId, localizer["Log_Header_Prep"], false));
            uiLogger.Report(new SyncProgressEvent(headId, "", true));

            var prepTasks = remotes.Select(async (remote, index) =>
            {
                await Task.Delay(index * 200, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                // Task-level indentation for per-drive status reporting.
                var taskId = Guid.NewGuid();
                uiLogger.Report(new SyncProgressEvent(taskId,
                    $"    {string.Format(localizer["Log_Stage0_Sanitize"], remote.FriendlyName)}", false));
                await sanitizeStage.RunAsync(remote, uiLogger, cancellationToken);
                uiLogger.Report(new SyncProgressEvent(taskId, "", true)); // Stop progress indicator.
                AdvanceProgress();

                cancellationToken.ThrowIfCancellationRequested();
                var consId = Guid.NewGuid();
                uiLogger.Report(new SyncProgressEvent(consId,
                    $"    {string.Format(localizer["Log_Stage1_Consolidate"], remote.FriendlyName)}", false));
                await consolidateStage.RunAsync(remote, uiLogger, cancellationToken);
                uiLogger.Report(new SyncProgressEvent(consId, "", true)); // Stop task animation.
                AdvanceProgress();
            });

            await Task.WhenAll(prepTasks);

            await crossAccountStage.RunAsync(remotes, master, uiLogger, AdvanceProgress, cancellationToken);

            progressUpdater.Report(100);
            Logger.Info("=== SESSION FINISHED SUCCESSFULLY ===");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Sync Engine failed!");
            throw;
        }
    }
}