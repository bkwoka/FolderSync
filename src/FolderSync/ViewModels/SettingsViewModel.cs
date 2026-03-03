using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using CommunityToolkit.Mvvm.Messaging;
using FolderSync.Messages;
using NLog;

namespace FolderSync.ViewModels;

public record LanguageItem(string DisplayName, string CultureCode);

/// <summary>
/// ViewModel for the Settings view, managing drive configurations, application preferences, and profile backup/restore.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IConfigService _configService;
    private readonly ITranslationService _localizer;
    private readonly IRcloneBootstrapper _bootstrapper;
    private readonly IFilePickerService _filePickerService;
    private readonly IUpdateService _updateService;
    private readonly IDriveOrchestratorService _driveOrchestrator;
    private readonly IProfileOrchestratorService _profileOrchestrator;

    public ObservableCollection<RemoteInfo> SavedRemotes { get; } = new();

    [ObservableProperty] private RemoteInfo? _selectedRemote;

    /// <summary>
    /// Temporary storage for a remote designated for a deletion/edit process.
    /// </summary>
    [ObservableProperty] private RemoteInfo? _remoteToProcess;

    [ObservableProperty] private string _pendingName = string.Empty;
    [ObservableProperty] private string _pendingEmail = string.Empty;
    [ObservableProperty] private string _pendingFolderId = string.Empty;
    private string _pendingToken = string.Empty;
    [ObservableProperty] private string _statusMessage;
    [ObservableProperty] private string _currentMasterName;
    [ObservableProperty] private string _oauthCountdownText = string.Empty;
    [ObservableProperty] private string _corruptedRemotesText = string.Empty;
    private List<RemoteInfo> _corruptedRemotesList = new();
    [ObservableProperty] private bool _isOauthModalVisible;
    [ObservableProperty] private bool _isConfigModalVisible;
    [ObservableProperty] private bool _isOverwriteModalVisible;
    [ObservableProperty] private bool _isDeleteModalVisible;
    [ObservableProperty] private bool _isVerifyingFolder;

    /// <summary>
    /// Indicates whether the system is currently scanning Google Drive for an existing "Google AI Studio" folder.
    /// </summary>
    [ObservableProperty] private bool _isAutoDetectingFolder;

    /// <summary>
    /// Tracks foreground processing for remote actions (Repair, Delete) to show progress within modals.
    /// </summary>
    [ObservableProperty] private bool _isProcessingRemoteAction;

    /// <summary>
    /// Tracks drive configuration save state to prevent concurrent modifications within the config modal.
    /// </summary>
    [ObservableProperty] private bool _isSavingDrive;

    [ObservableProperty] private bool _isIntegrityModalVisible;

    /// <summary>
    /// Controls the visibility of the manual update notification modal.
    /// </summary>
    [ObservableProperty] private bool _isUpdateModalVisible;

    [ObservableProperty] private bool _isBackupModalVisible;
    [ObservableProperty] private string _backupModalTitle = string.Empty;
    [ObservableProperty] private string _backupModalDescription = string.Empty;
    [ObservableProperty] private string _backupPassword = string.Empty;
    [ObservableProperty] private string _backupErrorMessage = string.Empty;
    [ObservableProperty] private bool _isBackupProcessing;

    private string _backupSelectedPath = string.Empty;
    private bool _isExportingOperation;

    /// <summary>
    /// Displays the current application version retrieved from the assembly metadata.
    /// </summary>
    [ObservableProperty]
    private string _appVersionDisplay = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "Unknown";
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string _updateMessage = string.Empty;
    [ObservableProperty] private string _updateUrl = string.Empty;

    [ObservableProperty] private bool _skipRenameWarningPreference;
    [ObservableProperty] private bool _skipDeleteWarningPreference;
    [ObservableProperty] private LanguageItem? _selectedLanguage;

    public List<LanguageItem> AvailableLanguages { get; } = new()
    {
        new LanguageItem("English", "en"),
        new LanguageItem("Polski", "pl")
    };

    private CancellationTokenSource? _oauthCts;
    private bool _isInitializing;

    public SettingsViewModel(IConfigService configService, ITranslationService localizer,
        IRcloneBootstrapper bootstrapper,
        IFilePickerService filePickerService, IUpdateService updateService,
        IDriveOrchestratorService driveOrchestrator, IProfileOrchestratorService profileOrchestrator)
    {
        _configService = configService;
        _localizer = localizer;
        _bootstrapper = bootstrapper;
        _filePickerService = filePickerService;
        _updateService = updateService;
        _driveOrchestrator = driveOrchestrator;
        _profileOrchestrator = profileOrchestrator;

        _statusMessage = _localizer["Status_Ready"];
        _currentMasterName = _localizer["Status_NotSet"];

        _ = LoadDataAndCheckIntegrityAsync();

        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (r, m) =>
        {
            if (!IsAppLocked) StatusMessage = _localizer["Status_Ready"];

            // Performance optimization: Update only local memory strings to avoid redundant configuration I/O.
            var master = SavedRemotes.FirstOrDefault(rm => rm.IsMaster);
            CurrentMasterName = master != null
                ? $"{master.FriendlyName} ({master.RcloneRemote})"
                : _localizer["Status_NotSet"];

            if (IsIntegrityModalVisible)
                StatusMessage = _localizer["Warning_IntegrityProblem"];
        });
    }

    private async Task<bool> SavePreferenceAsync(Action<AppConfig> updateAction)
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            updateAction(config);
            await _configService.SaveConfigAsync(config);
            return true;
        }
        catch (Exception ex)
        {
            // Preventing silent UI state desynchronization by reporting save failures.
            Logger.Error(ex, "Failed to persist application configuration preferences.");
            StatusMessage = _localizer["Error_CheckLogs"];
            return false;
        }
    }

    protected override void OnAppLockChanged(bool isLocked)
    {
        StatusMessage = isLocked ? _localizer["Status_AppLockedSync"] : _localizer["Status_Ready"];
        StartAddDriveCommand.NotifyCanExecuteChanged();
        StartExportProfileCommand.NotifyCanExecuteChanged();
        StartImportProfileCommand.NotifyCanExecuteChanged();
        StartDeleteRemoteCommand.NotifyCanExecuteChanged();
        SetAsMasterCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task LoadDataAndCheckIntegrityAsync()
    {
        _ = CheckForUpdatesAsync();
        try
        {
            var state = await _driveOrchestrator.GetDrivesStateAsync();

            _isInitializing = true;
            try
            {
                SkipRenameWarningPreference = state.Config.SkipRenameSyncWarning;
                SkipDeleteWarningPreference = state.Config.SkipDeleteSyncWarning;
                SelectedLanguage = AvailableLanguages.FirstOrDefault(l => l.CultureCode == state.Config.Language) ??
                                   AvailableLanguages[0];
            }
            finally
            {
                _isInitializing = false;
            }

            SavedRemotes.Clear();
            foreach (var remote in state.Config.Remotes) SavedRemotes.Add(remote);

            var master = state.Config.Remotes.FirstOrDefault(r => r.FolderId == state.Config.MasterRemoteId);
            CurrentMasterName = master != null
                ? $"{master.FriendlyName} ({master.RcloneRemote})"
                : _localizer["Status_NotSet"];
            foreach (var remote in SavedRemotes) remote.IsMaster = (remote.FolderId == state.Config.MasterRemoteId);

            if (!_bootstrapper.IsInstalled()) return;

            _corruptedRemotesList = state.CorruptedRemotes;
            if (_corruptedRemotesList.Any())
            {
                CorruptedRemotesText = string.Join(", ", _corruptedRemotesList.Select(r => r.FriendlyName));
                IsIntegrityModalVisible = true;
                StatusMessage = _localizer["Warning_IntegrityProblem"];
            }
        }
        catch (Exception ex)
        {
            // Fail-Visible: Inform the user if the drive state cannot be retrieved
            // due to hardware or I/O issues (e.g., blocked rclone process).
            Logger.Error(ex, "LoadDataAndCheckIntegrityAsync failed to retrieve drive state.");
            StatusMessage = _localizer["Error_CheckLogs"];
        }
    }

    [RelayCommand]
    private void IgnoreIntegrity()
    {
        IsIntegrityModalVisible = false;
        StatusMessage = _localizer["Status_IntegrityIgnored"];
    }

    [RelayCommand]
    private async Task AutoRepairIntegrity()
    {
        // Show progress indicator within the modal instead of closing it immediately
        // to provide continuous visual feedback during the repair process.
        IsProcessingRemoteAction = true;
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));

        try
        {
            await _profileOrchestrator.AutoRepairIntegrityAsync(_corruptedRemotesList);
            await LoadDataAndCheckIntegrityAsync();
            StatusMessage = _localizer["Success_GhostRemoved"];
            
            // Operation successful: Close the integrity modal.
            IsIntegrityModalVisible = false;
        }
        catch (Exception ex)
        {
            StatusMessage = _localizer["Error_AutoRepairFailed"];
            Logger.Error(ex, "AutoRepairIntegrity failed");
        }
        finally
        {
            IsProcessingRemoteAction = false;
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
        }
    }

    private bool CanAddDrive() => !IsAppLocked;

    private bool CanModifyRemote() => !IsAppLocked;

    [RelayCommand(CanExecute = nameof(CanAddDrive))]
    private async Task StartAddDrive()
    {
        // Global UI Lock: Prevent interference during sensitive setup operations.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));

        IsOauthModalVisible = true;
        OauthCountdownText = _localizer["OAuth_WaitingDefault"];
        _oauthCts = new CancellationTokenSource();
        _oauthCts.CancelAfter(TimeSpan.FromMinutes(5));
        _ = RunCountdownAsync(_oauthCts.Token);

        try
        {
            var result = await _driveOrchestrator.AuthorizeNewDriveAsync(_oauthCts.Token);
            _pendingToken = result.Token;
            PendingName = result.Name;
            PendingEmail = result.Email;
            PendingFolderId = string.Empty;

            IsOauthModalVisible = false;
            IsConfigModalVisible = true;

            // Trigger background scanning for existing folders to streamline the setup process.
            _ = PerformAutoDetectionAsync(_pendingToken);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = _localizer["Status_LoginCancelled"];
            IsOauthModalVisible = false;
            // Unlock UI if the authorization process is explicitly cancelled.
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
        }
        catch (Exception ex)
        {
            StatusMessage = _localizer["Error_GoogleAuth"];
            Logger.Error(ex, "OAuth Error");
            IsOauthModalVisible = false;
            // Ensure UI is unlocked on critical authentication failures.
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
        }
        finally
        {
            if (_oauthCts != null && !_oauthCts.IsCancellationRequested) _oauthCts.Cancel();
            _oauthCts?.Dispose();
            _oauthCts = null;
        }
    }

    /// <summary>
    /// Conducts a background search for the "Google AI Studio" folder and automatically populates the folder ID if found.
    /// </summary>
    private async Task PerformAutoDetectionAsync(string token)
    {
        IsAutoDetectingFolder = true;
        try
        {
            string? detectedId = await _driveOrchestrator.AutoDetectFolderIdAsync(token);
            if (!string.IsNullOrWhiteSpace(detectedId))
            {
                PendingFolderId = detectedId;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Background auto-detection failed.");
        }
        finally
        {
            IsAutoDetectingFolder = false;
        }
    }

    private async Task RunCountdownAsync(CancellationToken ct)
    {
        var timeLeft = TimeSpan.FromMinutes(5);
        while (timeLeft >= TimeSpan.Zero && !ct.IsCancellationRequested && IsOauthModalVisible)
        {
            OauthCountdownText = string.Format(_localizer["OAuth_TimeLeft"], timeLeft.ToString("mm\\:ss"));
            try
            {
                await Task.Delay(1000, ct);
                timeLeft -= TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [RelayCommand]
    private void CancelOauth() => _oauthCts?.Cancel();

    [RelayCommand]
    private void CancelConfig()
    {
        IsConfigModalVisible = false;
        StatusMessage = _localizer["Status_DriveAddCancelled"];
        // Re-enable UI interaction after config cancellation.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
    }

    [RelayCommand]
    private async Task ConfirmOverwriteAsync()
    {
        // Indicate saving state while keeping the overwrite warning modal open.
        IsSavingDrive = true;
        await FinalizeAddDrive(overwrite: true);
    }

    [RelayCommand]
    private void CancelOverwrite()
    {
        IsOverwriteModalVisible = false;
        StatusMessage = _localizer["Status_DriveOverwriteCancelled"];
        // Restore UI availability after cancelling overwrite operation.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
    }

    [RelayCommand]
    private async Task ConfirmConfigAsync()
    {
        if (string.IsNullOrWhiteSpace(PendingName))
        {
            StatusMessage = _localizer["Error_NameRequired"];
            return;
        }
        
        bool isNameSafe = PendingName.All(c => char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-');
        if (!isNameSafe)
        {
            StatusMessage = _localizer["Error_InvalidCharacters"];
            return;
        }

        if (string.IsNullOrWhiteSpace(PendingFolderId))
        {
            StatusMessage = _localizer["Error_FolderIdRequired"];
            return;
        }

        IsVerifyingFolder = true;
        try
        {
            bool folderExists = await _driveOrchestrator.VerifyFolderExistsAsync(_pendingToken, PendingFolderId);
            if (!folderExists)
            {
                StatusMessage = _localizer["Error_FolderNotFound"];
                return;
            }
        }
        finally
        {
            IsVerifyingFolder = false;
        }

        bool emailExists = SavedRemotes.Any(r => r.RcloneRemote.Equals(PendingEmail, StringComparison.OrdinalIgnoreCase));
        
        if (emailExists)
        {
            // Transition from configuration modal to overwrite warning modal.
            IsConfigModalVisible = false;
            IsOverwriteModalVisible = true;
        }
        else
        {
            // Start the save process directly and show progress in the configuration modal.
            IsSavingDrive = true;
            await FinalizeAddDrive(overwrite: false);
        }
    }

    private async Task FinalizeAddDrive(bool overwrite)
    {
        StatusMessage = _localizer["Status_SavingMesh"];
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));
        
        try
        {
            await _driveOrchestrator.AddNewDriveAsync(PendingName, PendingEmail, PendingFolderId, _pendingToken, overwrite);
            await LoadDataAndCheckIntegrityAsync();
            StatusMessage = _localizer["Success_DriveAdded"];
            
            // Successfully integrated: Close any open configuration or overwrite modals.
            IsConfigModalVisible = false;
            IsOverwriteModalVisible = false;
        }
        catch (Exception ex)
        {
            StatusMessage = _localizer["Error_DriveSaveFailed"];
            Logger.Error(ex, "FinalizeAddDrive failed");
        }
        finally
        {
            IsSavingDrive = false;
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
        }
    }

    // Parameterized method
    [RelayCommand(CanExecute = nameof(CanModifyRemote))]
    private void StartDeleteRemote(RemoteInfo item)
    {
        if (item == null) return;
        // Lock UI before presenting the deletion confirmation dialog.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));
        RemoteToProcess = item;
        SelectedRemote = item; // Highlight the selected row for UI correlation.
        IsDeleteModalVisible = true;
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsDeleteModalVisible = false;
        RemoteToProcess = null;
        StatusMessage = _localizer["Status_DriveDeleteCancelled"];
        // Ensure UI is unlocked when the deletion process is aborted.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
    }

    [RelayCommand]
    private async Task ConfirmDelete()
    {
        if (RemoteToProcess == null) return;
        
        // Activate progress indicator and keep the modal open during the deletion process
        // to ensure the user is aware of the background task status.
        IsProcessingRemoteAction = true;
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));

        var targetToRemove = RemoteToProcess;

        try
        {
            await _driveOrchestrator.DeleteDriveAsync(targetToRemove);
            await LoadDataAndCheckIntegrityAsync();
            StatusMessage = string.Format(_localizer["Success_DriveRemoved"], targetToRemove.FriendlyName);
            
            // Deletion successful: Close the modal.
            IsDeleteModalVisible = false;
        }
        catch (Exception ex)
        {
            StatusMessage = _localizer["Error_CheckLogs"];
            Logger.Error(ex, "Account deletion failed.");
        }
        finally
        {
            RemoteToProcess = null;
            IsProcessingRemoteAction = false;
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
        }
    }

    // Overload with row parameter
    [RelayCommand(CanExecute = nameof(CanModifyRemote))]
    private async Task SetAsMaster(RemoteInfo item)
    {
        if (item == null || item.IsMaster) return;
        
        // Locking UI for atomic metadata update in the Rclone config.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));
        
        try
        {
            await _driveOrchestrator.SetAsMasterAsync(item);
            await LoadDataAndCheckIntegrityAsync();
            StatusMessage = _localizer["Success_MasterChanged"];
        }
        catch (Exception ex)
        {
            // Inform the user about the failure to ensure configuration state transparency.
            StatusMessage = _localizer["Error_SetMasterFailed"];
            Logger.Error(ex, "Failed to change the Master drive configuration.");
        }
        finally
        {
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
        }
    }

    [RelayCommand]
    private void OpenFolderInBrowser(string folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId)) return;
        string url = $"https://drive.google.com/drive/folders/{folderId}";
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform
                    .Windows))
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices
                         .OSPlatform.OSX))
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to open the system browser for the requested URL.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddDrive))]
    private async Task StartExportProfile()
    {
        var path = await _filePickerService.SaveFileDialogAsync(_localizer["Backup_SaveTitle"], "FolderSync_Profile",
            ".fsbak");
        if (string.IsNullOrEmpty(path)) return;

        // Secure UI lock during configuration data extraction.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));
        
        _backupSelectedPath = path;
        _isExportingOperation = true;
        BackupModalTitle = _localizer["Backup_ExportTitle"];
        BackupModalDescription = _localizer["Backup_ExportDesc"];
        BackupPassword = string.Empty;
        BackupErrorMessage = string.Empty;
        IsBackupModalVisible = true;
    }

    [RelayCommand(CanExecute = nameof(CanAddDrive))]
    private async Task StartImportProfile()
    {
        var path = await _filePickerService.OpenFileDialogAsync(_localizer["Backup_OpenTitle"], new[] { ".fsbak" });
        if (string.IsNullOrEmpty(path)) return;

        // Establish UI lock before importing a security-sensitive profile.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));
        
        _backupSelectedPath = path;
        _isExportingOperation = false;
        BackupModalTitle = _localizer["Backup_ImportTitle"];
        BackupModalDescription = _localizer["Backup_ImportDesc"];
        BackupPassword = string.Empty;
        BackupErrorMessage = string.Empty;
        IsBackupModalVisible = true;
    }

    [RelayCommand]
    private void CancelBackup()
    {
        IsBackupModalVisible = false;
        BackupPassword = string.Empty;
        // Release UI lock on backup/restore cancellation.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
    }

    [RelayCommand]
    private async Task ConfirmBackupOperation()
    {
        BackupErrorMessage = string.Empty;
        if (BackupPassword.Length < AppConstants.MinPasswordLength)
        {
            BackupErrorMessage = _localizer["Error_PasswordShort"];
            return;
        }

        IsBackupProcessing = true;

        try
        {
            if (_isExportingOperation)
            {
                await _profileOrchestrator.ExportProfileAsync(BackupPassword, _backupSelectedPath);
                StatusMessage = _localizer["Success_Exported"];
            }
            else
            {
                await _profileOrchestrator.ImportProfileAsync(BackupPassword, _backupSelectedPath);
                StatusMessage = _localizer["Success_Imported"];
                await LoadDataAndCheckIntegrityAsync();
                if (SelectedLanguage != null)
                {
                    _localizer.Culture = new System.Globalization.CultureInfo(SelectedLanguage.CultureCode);
                    WeakReferenceMessenger.Default.Send(new LanguageChangedMessage(SelectedLanguage.CultureCode));
                }
            }

            IsBackupModalVisible = false;
        }
        catch (UnauthorizedAccessException)
        {
            BackupErrorMessage = _localizer["Error_WrongPassword"];
        }
        catch (InvalidOperationException)
        {
            BackupErrorMessage = _localizer["Error_InvalidFile"];
        }
        catch (Exception ex)
        {
            BackupErrorMessage = _localizer["Error_BackupUnknown"];
            Logger.Error(ex, "Profile backup/restore operation failed.");
        }
        finally
        {
            IsBackupProcessing = false;
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
            BackupPassword = string.Empty;
        }
    }

    // Update Modal integration
    [RelayCommand]
    private void ShowUpdateModal()
    {
        IsUpdateModalVisible = true;
    }

    [RelayCommand]
    private void CancelUpdate()
    {
        IsUpdateModalVisible = false;
    }

    [RelayCommand]
    private void ConfirmUpdate()
    {
        IsUpdateModalVisible = false;
        if (string.IsNullOrEmpty(UpdateUrl)) return;

        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform
                    .Windows))
                Process.Start(new ProcessStartInfo { FileName = UpdateUrl, UseShellExecute = true });
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices
                         .OSPlatform.OSX))
                Process.Start("open", UpdateUrl);
            else
                Process.Start("xdg-open", UpdateUrl);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to open the system browser for updates.");
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        var updateInfo = await _updateService.CheckForUpdatesAsync();
        if (updateInfo != null && updateInfo.IsUpdateAvailable)
        {
            UpdateUrl = updateInfo.ReleaseUrl;
            UpdateMessage = string.Format(_localizer["Update_Available"], updateInfo.VersionName);
            IsUpdateAvailable = true;
        }
    }

    partial void OnSelectedLanguageChanged(LanguageItem? value)
    {
        if (!_isInitializing && value != null)
        {
            // Although ComboBox state rollback is complex without triggering additional event loops,
            // we must ensure the application reacts to configuration save failures.
            Task.Run(async () =>
            {
                bool success = await SavePreferenceAsync(c => c.Language = value.CultureCode);
                if (success)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _localizer.Culture = new System.Globalization.CultureInfo(value.CultureCode);
                        WeakReferenceMessenger.Default.Send(new LanguageChangedMessage(value.CultureCode));
                    });
                }
            });
        }
    }

    partial void OnSkipRenameWarningPreferenceChanged(bool value)
    {
        if (!_isInitializing)
        {
            Task.Run(async () =>
            {
                bool success = await SavePreferenceAsync(c => c.SkipRenameSyncWarning = value);
                if (!success)
                {
                    // Rollback UI state if the disk persistence operation failed, suppressing further event triggers.
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _isInitializing = true;
                        SkipRenameWarningPreference = !value;
                        _isInitializing = false;
                    });
                }
            });
        }
    }

    partial void OnSkipDeleteWarningPreferenceChanged(bool value)
    {
        if (!_isInitializing)
        {
            Task.Run(async () =>
            {
                bool success = await SavePreferenceAsync(c => c.SkipDeleteSyncWarning = value);
                if (!success)
                {
                    // Revert UI toggle state because the configuration update could not be finalized on disk.
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _isInitializing = true;
                        SkipDeleteWarningPreference = !value;
                        _isInitializing = false;
                    });
                }
            });
        }
    }
}