using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FolderSync.Messages;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using System.Collections.Generic;
using NLog;
using Avalonia;
using FolderSync.Helpers;

namespace FolderSync.ViewModels;

/// <summary>
/// ViewModel for the Synchronization view, handling the sync process and logging.
/// </summary>
public partial class SyncViewModel : ViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ISyncEngine _syncEngine;
    private readonly IConfigService _configService;
    private readonly IRcloneService _rcloneService;
    private readonly ITranslationService _localizer;

    [ObservableProperty] private string _status;
    [ObservableProperty] private double _progressValue = 0;
    [ObservableProperty] private string _syncButtonText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSyncCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelSyncCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _isValidationModalVisible;
    [ObservableProperty] private string _validationMessage = string.Empty;

    /// <summary>
    /// Gets the collection of sync logs. Uses <see cref="RangeObservableCollection{T}"/> for performance.
    /// </summary>
    public RangeObservableCollection<LogEntry> Logs { get; } = new();

    private CancellationTokenSource? _syncCts;

    public SyncViewModel(ISyncEngine syncEngine, IConfigService configService, IRcloneService rcloneService,
        ITranslationService localizer)
    {
        _syncEngine = syncEngine;
        _configService = configService;
        _rcloneService = rcloneService;
        _localizer = localizer;

        _status = _localizer["Status_Ready"];
        _syncButtonText = _localizer["Sync_ButtonStart"];

        // Listen for language changes to update UI text dynamically
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (r, m) =>
        {
            if (!IsBusy)
            {
                Status = _localizer["Status_Ready"];
                SyncButtonText = _localizer["Sync_ButtonStart"];
            }
        });
    }

    [RelayCommand]
    private void CloseValidationModal() => IsValidationModalVisible = false;

    private bool CanStartSync() => !IsBusy && !IsAppLocked;
    private bool CanCancelSync() => IsBusy;

    /// <summary>
    /// Refreshes command availability when the global lock state changes.
    /// </summary>
    protected override void OnAppLockChanged(bool isLocked)
    {
        StartSyncCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Cancels the ongoing synchronization process.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelSync))]
    private void CancelSync()
    {
        try
        {
            // Protected against ObjectDisposedException
            if (_syncCts != null && !_syncCts.IsCancellationRequested)
            {
                _syncCts.Cancel();
                AddLog(new SyncProgressEvent(Guid.NewGuid(), _localizer["Log_SyncCancelledByUser"], false, LogEntryType.Warning));
                SyncButtonText = _localizer["Sync_ButtonStopping"];
            }
        }
        catch (ObjectDisposedException)
        {
            // Ignore if the object has already been disposed during application lifecycle.
        }
    }

    /// <summary>
    /// Validates prerequisites and starts the synchronization process.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartSync))]
    private async Task StartSync()
    {
        // UI block guard before async tasks
        IsBusy = true;
        SyncButtonText = _localizer["Sync_ButtonSyncing"];
        Status = _localizer["Status_SyncInProgress"];

        var config = await _configService.LoadConfigAsync();
        var validationError = await ValidateSyncPrerequisitesAsync(config);
        if (validationError != null)
        {
            ValidationMessage = validationError;
            IsValidationModalVisible = true;
            // Revert busy state if validation fails.
            IsBusy = false;
            SyncButtonText = _localizer["Sync_ButtonStart"];
            Status = _localizer["Status_Ready"];
            return;
        }

        ProgressValue = 0;
        Logs.Clear();

        _syncCts = new CancellationTokenSource();
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));

        try
        {
            var master = config.Remotes.FirstOrDefault(r => r.FolderId == config.MasterRemoteId);
            if (master == null)
            {
                Logger.Warn("Master remote configuration is missing during sync initiation.");
                Status = _localizer["Error_MasterNotExist"];
                IsBusy = false;
                SyncButtonText = _localizer["Sync_ButtonStart"];
                WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
                return;
            }

            // Using Progress<T> which automatically marshals reports back to the UI thread
            var loggerProgress = new Progress<SyncProgressEvent>(message => AddLog(message));
            var progressUpdater = new Progress<double>(progress => ProgressValue = progress);

            await _syncEngine.RunFullSync(
                config.Remotes,
                master,
                loggerProgress,
                progressUpdater,
                _syncCts.Token
            );

            await Task.Delay(500, CancellationToken.None);
            Status = string.Format(_localizer["Status_LastSync"], DateTime.Now.ToString("dd.MM.yyyy HH:mm"));
        }
        catch (OperationCanceledException)
        {
            Logger.Info("Synchronization cancelled by user.");
            Status = _localizer["Status_SyncAborted"];
            ProgressValue = 0;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Synchronization failed due to a critical error.");
            Status = _localizer["Error_SyncCritical"];
            AddLog(new SyncProgressEvent(Guid.NewGuid(), string.Format(_localizer["Log_Error"], ex.Message), false, LogEntryType.Warning));
        }
        finally
        {
            _syncCts?.Dispose();
            _syncCts = null;

            InvokeOnUIThread(() =>
            {
                ProgressValue = 0;
                IsBusy = false;
                SyncButtonText = _localizer["Sync_ButtonStart"];
                WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
            });
        }
    }

    private void InvokeOnUIThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    // Fast reference dictionary for active tasks
    private readonly Dictionary<Guid, LogEntry> _activeTasks = new();

    /// <summary>
    /// Processes a sync progress event, adding new log entries or updating existing active tasks.
    /// </summary>
    public void AddLog(SyncProgressEvent evt)
    {
        // 1. TASK COMPLETION: Set activity to false if the engine signals the task is finished.
        if (evt.IsFinished)
        {
            if (_activeTasks.TryGetValue(evt.TaskId, out var existingEntry))
            {
                InvokeOnUIThread(() => existingEntry.IsActive = false);
                _activeTasks.Remove(evt.TaskId);
            }
            return;
        }

        // 2. TASK INITIALIZATION / STANDARD LOGGING
        string rawMessage = evt.Message;

        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            InvokeOnUIThread(() => Logs.Add(new LogEntry { Text = "", Id = evt.TaskId }));
            return;
        }

        if (rawMessage.StartsWith("\n"))
        {
            InvokeOnUIThread(() => Logs.Add(new LogEntry { Text = "", Id = Guid.NewGuid() }));
            rawMessage = rawMessage.Substring(1);
        }
        
        bool hasTrailingNewline = false;
        if (rawMessage.EndsWith("\n"))
        {
            rawMessage = rawMessage.Substring(0, rawMessage.Length - 1);
            hasTrailingNewline = true;
        }

        var entry = new LogEntry
        {
            Id = evt.TaskId,
            Text = rawMessage,
            Type = evt.Type,
            IndentLevel = evt.IndentLevel,
            IsActive = true // Trigger the task spinner animation.
        };

        // Store the entry reference to handle completion updates.
        _activeTasks[evt.TaskId] = entry;

        InvokeOnUIThread(() =>
        {
            Logs.Add(entry);
            if (hasTrailingNewline)
            {
                Logs.Add(new LogEntry { Text = "", Id = Guid.NewGuid() });
            }
            if (Logs.Count >= 550) Logs.RemoveRange(0, 50);
        });
    }

    /// <summary>
    /// Performs asynchronous validation of current configuration and Rclone state.
    /// </summary>
    private async Task<string?> ValidateSyncPrerequisitesAsync(AppConfig config)
    {
        if (config.Remotes == null || config.Remotes.Count == 0) return _localizer["Error_NoDrives"];
        if (string.IsNullOrEmpty(config.MasterRemoteId)) return _localizer["Error_NoMaster"];
        if (config.Remotes.Count < 2) return _localizer["Error_NeedTwoDrives"];

        var master = config.Remotes.FirstOrDefault(r => r.FolderId == config.MasterRemoteId);
        if (master == null) return _localizer["Error_MasterNotExist"];

        try
        {
            var actualRcloneRemotes = await _rcloneService.GetConfiguredRemotesAsync();
            bool hasCorruptedRemotes = config.Remotes.Any(r => !actualRcloneRemotes.Contains(r.RcloneRemote));

            if (hasCorruptedRemotes) return _localizer["Error_Integrity_Details"];
        }
        catch (Exception ex)
        {
            return string.Format(_localizer["Error_RcloneQueryFailed"], ex.Message);
        }

        return null;
    }
}