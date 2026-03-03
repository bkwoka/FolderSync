using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FolderSync.Messages;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.ViewModels.Settings;

/// <summary>
/// ViewModel responsible for securely exporting and importing application profiles.
/// </summary>
public partial class ProfileBackupViewModel : ViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IProfileOrchestratorService _profileOrchestrator;
    private readonly IFilePickerService _filePickerService;
    private readonly ITranslationService _localizer;

    [ObservableProperty] private bool _isBackupModalVisible;
    [ObservableProperty] private string _backupModalTitle = string.Empty;
    [ObservableProperty] private string _backupModalDescription = string.Empty;
    [ObservableProperty] private string _backupPassword = string.Empty;
    [ObservableProperty] private string _backupErrorMessage = string.Empty;
    [ObservableProperty] private bool _isBackupProcessing;

    private string _backupSelectedPath = string.Empty;
    private bool _isExportingOperation;

    public event Action<string>? StatusMessageChanged;
    public event Func<Task>? ProfileImported;

    public ProfileBackupViewModel(
        IProfileOrchestratorService profileOrchestrator,
        IFilePickerService filePickerService,
        ITranslationService localizer)
    {
        _profileOrchestrator = profileOrchestrator;
        _filePickerService = filePickerService;
        _localizer = localizer;
    }

    protected override void OnAppLockChanged(bool isLocked)
    {
        StartExportProfileCommand.NotifyCanExecuteChanged();
        StartImportProfileCommand.NotifyCanExecuteChanged();
    }

    private bool CanBackup() => !IsAppLocked;

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private async Task StartExportProfile()
    {
        var path = await _filePickerService.SaveFileDialogAsync(_localizer["Backup_SaveTitle"], "FolderSync_Profile", ".fsbak");
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

    [RelayCommand(CanExecute = nameof(CanBackup))]
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
                StatusMessageChanged?.Invoke(_localizer["Success_Exported"]);
            }
            else
            {
                await _profileOrchestrator.ImportProfileAsync(BackupPassword, _backupSelectedPath);
                StatusMessageChanged?.Invoke(_localizer["Success_Imported"]);
                
                if (ProfileImported != null)
                {
                    await ProfileImported.Invoke();
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
}
