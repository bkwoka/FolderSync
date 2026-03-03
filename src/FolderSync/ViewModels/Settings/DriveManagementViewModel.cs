using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FolderSync.Messages;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.ViewModels.Settings;

/// <summary>
/// ViewModel responsible for managing the list of configured drives, master selection,
/// deletion, and database integrity auto-repair.
/// </summary>
public partial class DriveManagementViewModel : ViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IDriveOrchestratorService _driveOrchestrator;
    private readonly IProfileOrchestratorService _profileOrchestrator;
    private readonly IRcloneBootstrapper _bootstrapper;
    private readonly ITranslationService _localizer;

    public ObservableCollection<RemoteInfo> SavedRemotes { get; } = new();

    [ObservableProperty] private RemoteInfo? _selectedRemote;

    /// <summary>
    /// Temporary storage for a remote designated for a deletion/edit process.
    /// </summary>
    [ObservableProperty] private RemoteInfo? _remoteToProcess;

    [ObservableProperty] private string _currentMasterName;
    [ObservableProperty] private string _corruptedRemotesText = string.Empty;
    private List<RemoteInfo> _corruptedRemotesList = new();

    [ObservableProperty] private bool _isIntegrityModalVisible;

    /// <summary>
    /// Tracks foreground processing for remote actions (Repair, Delete) to show progress within modals.
    /// </summary>
    [ObservableProperty] private bool _isProcessingRemoteAction;

    [ObservableProperty] private bool _isDeleteModalVisible;

    public event Action<string>? StatusMessageChanged;

    public DriveManagementViewModel(
        IDriveOrchestratorService driveOrchestrator,
        IProfileOrchestratorService profileOrchestrator,
        IRcloneBootstrapper bootstrapper,
        ITranslationService localizer)
    {
        _driveOrchestrator = driveOrchestrator;
        _profileOrchestrator = profileOrchestrator;
        _bootstrapper = bootstrapper;
        _localizer = localizer;

        _currentMasterName = _localizer["Status_NotSet"];

        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (r, m) =>
        {
            // Performance optimization: Update only local memory strings to avoid redundant configuration I/O.
            var master = SavedRemotes.FirstOrDefault(rm => rm.IsMaster);
            CurrentMasterName = master != null
                ? $"{master.FriendlyName} ({master.RcloneRemote})"
                : _localizer["Status_NotSet"];
        });
    }

    protected override void OnAppLockChanged(bool isLocked)
    {
        StartDeleteRemoteCommand.NotifyCanExecuteChanged();
        SetAsMasterCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadDataAndCheckIntegrityAsync()
    {
        try
        {
            var state = await _driveOrchestrator.GetDrivesStateAsync();

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
                StatusMessageChanged?.Invoke(_localizer["Warning_IntegrityProblem"]);
            }
        }
        catch (Exception ex)
        {
            // Fail-Visible: Inform the user if the drive state cannot be retrieved
            // due to hardware or I/O issues (e.g., blocked rclone process).
            Logger.Error(ex, "LoadDataAndCheckIntegrityAsync failed to retrieve drive state.");
            StatusMessageChanged?.Invoke(_localizer["Error_CheckLogs"]);
        }
    }

    [RelayCommand]
    private void IgnoreIntegrity()
    {
        IsIntegrityModalVisible = false;
        StatusMessageChanged?.Invoke(_localizer["Status_IntegrityIgnored"]);
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
            StatusMessageChanged?.Invoke(_localizer["Success_GhostRemoved"]);
            
            // Operation successful: Close the integrity modal.
            IsIntegrityModalVisible = false;
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(_localizer["Error_AutoRepairFailed"]);
            Logger.Error(ex, "AutoRepairIntegrity failed");
        }
        finally
        {
            IsProcessingRemoteAction = false;
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
        }
    }

    private bool CanModifyRemote() => !IsAppLocked;

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
        StatusMessageChanged?.Invoke(_localizer["Status_DriveDeleteCancelled"]);
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
            StatusMessageChanged?.Invoke(string.Format(_localizer["Success_DriveRemoved"], targetToRemove.FriendlyName));
            
            // Deletion successful: Close the modal.
            IsDeleteModalVisible = false;
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(_localizer["Error_CheckLogs"]);
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
            StatusMessageChanged?.Invoke(_localizer["Success_MasterChanged"]);
        }
        catch (Exception ex)
        {
            // Inform the user about the failure to ensure configuration state transparency.
            StatusMessageChanged?.Invoke(_localizer["Error_SetMasterFailed"]);
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
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                System.Diagnostics.Process.Start("open", url);
            else
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to open the system browser for the requested URL.");
        }
    }

}
