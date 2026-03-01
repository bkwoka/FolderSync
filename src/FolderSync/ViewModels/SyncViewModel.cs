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

    // Icon mapping configuration
    private static readonly Dictionary<string, (string Path, string Color)> _iconMap = new()
    {
        {
            "⚠️", ("M12 5.99L19.53 19H4.47L12 5.99M12 2L1 21H23L12 2ZM13 16H11V18H13V16ZM13 10H11V15H13V10Z", "#E05C5C")
        },
        {
            "📦",
            ("M20 2H4C2.9 2 2 2.9 2 4V7C2 7.8 2.5 8.5 3 8.8V20C3 21.1 3.9 22 5 22H19C20.1 22 21 21.1 21 20V8.8C21.5 8.5 22 7.8 22 7V4C22 2.9 21.1 2 20 2ZM19 20H5V9H19V20ZM20 7H4V4H20V7ZM9 12H15V14H9V12Z",
                "#8899A6")
        },
        {
            "🔍",
            ("M15.5 14H14.71L14.43 13.73C15.41 12.59 16 11.11 16 9.5C16 5.91 13.09 3 9.5 3C5.91 3 3 5.91 3 9.5C3 13.09 5.91 16 9.5 16C11.11 16 12.59 15.41 13.73 14.43L14 14.71V15.5L19 20.49L20.49 19L15.5 14ZM9.5 14C7.01 14 5 11.99 5 9.5C5 7.01 7.01 5 9.5 5C11.99 5 14 7.01 14 9.5C14 11.99 11.99 14 9.5 14Z",
                "#0699BE")
        },
        { "⇄", ("M22 8L18 4V7H3V9H18V12L22 8ZM2 16L6 20V17H21V15H6V12L2 16Z", "#0699BE") },
        { "⬇", ("M19 9H15V3H9V9H5L12 16L19 9ZM5 18V20H19V18H5Z", "#6CCC3C") },
        { "⬆", ("M9 16H15V10H19L12 3L5 10H9V16ZM5 18V20H19V18H5Z", "#6CCC3C") }
    };

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
                AddLog(new SyncProgressEvent(Guid.NewGuid(), $"⚠️ {_localizer["Log_SyncCancelledByUser"]}", false));
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
            AddLog(new SyncProgressEvent(Guid.NewGuid(), $"⚠️ {string.Format(_localizer["Log_Error"], ex.Message)}",
                false));
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

        int leadingSpaces = 0;
        while (leadingSpaces < rawMessage.Length && rawMessage[leadingSpaces] == ' ') leadingSpaces++;

        double margin = leadingSpaces * 6;
        string cleanText = rawMessage.TrimStart();
        string? iconPath = null;
        string iconColor = "#e4eaec";

        foreach (var kvp in _iconMap)
        {
            if (cleanText.StartsWith(kvp.Key))
            {
                iconPath = kvp.Value.Path;
                iconColor = kvp.Value.Color;
                cleanText = cleanText.Substring(kvp.Key.Length).TrimStart();
                break;
            }
        }

        var entry = new LogEntry
        {
            Id = evt.TaskId,
            Text = cleanText,
            IconPath = iconPath,
            IconColor = iconColor,
            LeftMargin = margin,
            IsActive = true // Trigger the task spinner animation.
        };

        // Store the entry reference to handle completion updates.
        _activeTasks[evt.TaskId] = entry;

        InvokeOnUIThread(() =>
        {
            Logs.Add(entry);
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