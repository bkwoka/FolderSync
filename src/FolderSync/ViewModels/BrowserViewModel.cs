using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FolderSync.Exceptions;
using FolderSync.Messages;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.ViewModels;

/// <summary>
/// ViewModel for the File Browser view, allowing users to browse, rename, and delete conversation files
/// across multiple configured Google Drive remotes.
/// </summary>
public partial class BrowserViewModel : ViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IRcloneService _rcloneService;
    private readonly IConfigService _configService;
    private readonly IRenameOrchestratorService _renameService;
    private readonly IDeleteOrchestratorService _deleteService;
    private readonly ITranslationService _localizer;

    [ObservableProperty] private ObservableCollection<RcloneItem> _files = new();
    private List<RcloneItem> _allFetchedFiles = new();

    public ObservableCollection<RemoteInfo> AvailableRemotes { get; } = new();

    [ObservableProperty] private RemoteInfo? _selectedRemote;

    [ObservableProperty] private bool _canModify;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    [ObservableProperty] private string _statusMessage;
    [ObservableProperty] private string _currentDriveInfo;
    [ObservableProperty] private bool _showOnlyConversations = true;

    [ObservableProperty] private bool _isWarningModalVisible;
    [ObservableProperty] private bool _isInputModalVisible;
    [ObservableProperty] private string _newFileName = string.Empty;
    [ObservableProperty] private bool _skipWarningCheckbox;
    [ObservableProperty] private bool _isRenaming;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasRenameError))]
    private string _renameErrorMessage = string.Empty;

    public bool HasRenameError => !string.IsNullOrEmpty(RenameErrorMessage);

    [ObservableProperty] private bool _isDeleteModalVisible;
    [ObservableProperty] private bool _isDeleting;
    [ObservableProperty] private bool _deleteAttachmentsToggle;
    [ObservableProperty] private bool _showDeleteSyncWarning;
    [ObservableProperty] private bool _skipDeleteWarningCheckbox;
    [ObservableProperty] private string _deleteTargetName = string.Empty;

    private RcloneItem? _fileToProcess;

    private string? _lastViewedFolderId;

    // File loading timeout control
    private CancellationTokenSource? _refreshCts;

    public BrowserViewModel(IRcloneService rcloneService, IConfigService configService,
        IRenameOrchestratorService renameService, IDeleteOrchestratorService deleteService,
        ITranslationService localizer)
    {
        _rcloneService = rcloneService;
        _configService = configService;
        _renameService = renameService;
        _deleteService = deleteService;
        _localizer = localizer;

        _statusMessage = _localizer["Status_LoadingInfo"];
        _currentDriveInfo = _localizer["Status_Checking"];

        // Refresh UI state and filter when language changes
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (r, m) =>
        {
            if (!IsLoading && !IsRenaming && !IsDeleting)
            {
                if (!string.IsNullOrEmpty(_lastViewedFolderId)) ApplyFilter();
                else
                {
                    StatusMessage = _localizer["Status_LoadingInfo"];
                    CurrentDriveInfo = _localizer["Status_Checking"];
                }
            }
        });
    }

    private bool CanRefresh() => !IsLoading && !IsAppLocked;

    /// <summary>
    /// Refreshes command availability when the global lock state changes.
    /// </summary>
    protected override void OnAppLockChanged(bool isLocked)
    {
        RefreshCommand.NotifyCanExecuteChanged();
    }

    partial void OnShowOnlyConversationsChanged(bool value) => ApplyFilter();

    partial void OnSelectedRemoteChanged(RemoteInfo? value)
    {
        if (value == null || IsLoading || IsAppLocked) return;
        _ = RefreshAsync();
    }

    /// <summary>
    /// Synchronizes the list of available remotes from configuration and performs initial refresh if necessary.
    /// </summary>
    [RelayCommand]
    public async Task AutoRefreshAsync()
    {
        if (IsLoading) return;
        var config = await _configService.LoadConfigAsync();

        var toRemove = AvailableRemotes.Where(ar => !config.Remotes.Any(cr => cr.FolderId == ar.FolderId)).ToList();
        foreach (var r in toRemove) AvailableRemotes.Remove(r);

        foreach (var cr in config.Remotes)
        {
            if (AvailableRemotes.All(ar => ar.FolderId != cr.FolderId))
            {
                AvailableRemotes.Add(cr);
            }
        }

        var master = AvailableRemotes.FirstOrDefault(r => r.FolderId == config.MasterRemoteId);

        if (SelectedRemote == null && master != null)
        {
            SelectedRemote = master;
        }
        else if (SelectedRemote != null)
        {
            // Modification allowed only on the Master drive to ensure sync consistency
            CanModify = (config.MasterRemoteId == SelectedRemote.FolderId);

            if (Files.Count == 0 || _lastViewedFolderId != SelectedRemote.FolderId)
            {
                await RefreshAsync();
            }
            else
            {
                CurrentDriveInfo =
                    $"{SelectedRemote.FriendlyName} ({SelectedRemote.Email ?? SelectedRemote.RcloneRemote})";
            }
        }
    }

    /// <summary>
    /// Fetches the file list from the currently selected Rclone remote.
    /// Incorporates a 2-minute timeout and cancellable operation.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (SelectedRemote == null) return;

        IsLoading = true;
        StatusMessage = _localizer["Status_ConnectingDrive"];
        _allFetchedFiles.Clear();
        Files = new();

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource(TimeSpan.FromMinutes(2)); // P2.2: Protection against I/O hang

        try
        {
            var config = await _configService.LoadConfigAsync();
            CanModify = (SelectedRemote.FolderId == config.MasterRemoteId);

            var actualRcloneRemotes = await _rcloneService.GetConfiguredRemotesAsync(_refreshCts.Token);
            if (!actualRcloneRemotes.Contains(SelectedRemote.RcloneRemote))
            {
                StatusMessage = _localizer["Error_DriveIntegrity"];
                CurrentDriveInfo = _localizer["Status_Corrupted"];
                return;
            }

            _lastViewedFolderId = SelectedRemote.FolderId;
            CurrentDriveInfo = $"{SelectedRemote.FriendlyName} ({SelectedRemote.Email ?? SelectedRemote.RcloneRemote})";
            StatusMessage = _localizer["Status_DownloadingFileList"];

            string path = $"{SelectedRemote.RcloneRemote},root_folder_id={SelectedRemote.FolderId}:";
            // Cancellation token propagation
            var items = await _rcloneService.ListItemsAsync(path, false, _refreshCts.Token);
            _allFetchedFiles = items.Where(f => !f.IsDir).ToList();
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            Logger.Warn("File list refresh timed out.");
            StatusMessage = _localizer["Error_OperationTimeout"];
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load files from remote.");
            StatusMessage = _localizer["Error_DownloadFailed"];
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Filters the fetched file list based on user preferences and performs an atomic collection swap for UI performance.
    /// </summary>
    private void ApplyFilter()
    {
        var filteredList = _allFetchedFiles
            .Where(f => !ShowOnlyConversations || f.IsConversation)
            .OrderByDescending(f => f.ModTime)
            .ToList();

        // Atomic swap to minimize UI refresh impact
        Files = new ObservableCollection<RcloneItem>(filteredList);

        StatusMessage = string.Format(_localizer["Status_ShowingResults"], filteredList.Count, _allFetchedFiles.Count);
    }

    [RelayCommand]
    private async Task StartDelete(RcloneItem item)
    {
        if (!CanModify) return;

        // UI Lockdown: Prevent any background process interference while the delete modal is active.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));

        _fileToProcess = item;
        DeleteTargetName = item.Name;
        DeleteAttachmentsToggle = false;
        IsDeleting = false;

        var config = await _configService.LoadConfigAsync();
        ShowDeleteSyncWarning = !config.SkipDeleteSyncWarning;
        SkipDeleteWarningCheckbox = config.SkipDeleteSyncWarning;
        IsDeleteModalVisible = true;
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsDeleteModalVisible = false;
        _fileToProcess = null;
        // Restore UI availability after the cancellation of the deletion process.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
    }

    /// <summary>
    /// Orchestrates global deletion of a file across the mesh.
    /// Incorporates a 2-minute timeout to protect UI thread from hanging I/O.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmDelete()
    {
        if (_fileToProcess == null || !CanModify)
        {
            // Failsafe UI release if state is inconsistent.
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
            return;
        }

        IsDeleting = true;
        StatusMessage = _localizer["Status_DeletingMesh"];

        // GUARD: Prevent indefinite UI lock by imposing a 2-minute hard timeout on Google API requests
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            var config = await _configService.LoadConfigAsync();
            if (config.SkipDeleteSyncWarning != SkipDeleteWarningCheckbox)
            {
                config.SkipDeleteSyncWarning = SkipDeleteWarningCheckbox;
                await _configService.SaveConfigAsync(config);
            }

            var master = config.Remotes.FirstOrDefault(r => r.FolderId == config.MasterRemoteId);
            if (master != null)
            {
                await _deleteService.DeleteConversationAsync(_fileToProcess.Name, DeleteAttachmentsToggle, master,
                    config.Remotes, timeoutCts.Token);

                _allFetchedFiles.Remove(_fileToProcess);
                Files.Remove(_fileToProcess);
                StatusMessage = _localizer["Success_ConversationDeleted"];
                IsDeleteModalVisible = false;
            }
        }
        catch (PartialDeletionException partialEx)
        {
            // Handle partial success: inform the user which remotes failed
            StatusMessage = string.Format(_localizer["Error_PartialDelete"], string.Join(", ", partialEx.FailedRemotes));
            Logger.Warn("Partial deletion occurred. Leftovers on: {0}", string.Join(", ", partialEx.FailedRemotes));
            
            // If the currently viewed remote was successful, remove it from the list anyway to reflect local state
            if (_fileToProcess != null && SelectedRemote != null && !partialEx.FailedRemotes.Contains(SelectedRemote.FriendlyName))
            {
                _allFetchedFiles.Remove(_fileToProcess);
                Files.Remove(_fileToProcess);
            }
            
            IsDeleteModalVisible = false;
        }
        catch (OperationCanceledException)
        {
            // Localization via .resx
            StatusMessage = _localizer["Error_OperationTimeout"];
            Logger.Error("Delete operation timed out after 2 minutes.");
        }
        catch (Exception ex)
        {
            StatusMessage = _localizer["Error_DeleteFailed"];
            Logger.Error(ex, "Delete operation failed.");
        }
        finally
        {
            _fileToProcess = null;
            IsDeleting = false;
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
        }
    }

    [RelayCommand]
    private async Task StartRename(RcloneItem item)
    {
        if (!CanModify) return;

        // Secure UI State: Lock interface to ensure atomic rename operation lifecycle.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));

        _fileToProcess = item;
        NewFileName = Path.GetFileNameWithoutExtension(item.Name);
        RenameErrorMessage = string.Empty;
        IsRenaming = false;

        var config = await _configService.LoadConfigAsync();
        SkipWarningCheckbox = config.SkipRenameSyncWarning;

        if (config.SkipRenameSyncWarning) IsInputModalVisible = true;
        else IsWarningModalVisible = true;
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsWarningModalVisible = false;
        IsInputModalVisible = false;
        _fileToProcess = null;
        RenameErrorMessage = string.Empty;
        IsRenaming = false;
        // Release global UI lock after renaming exit.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
    }

    [RelayCommand]
    private async Task ContinueToInput()
    {
        IsWarningModalVisible = false;
        var config = await _configService.LoadConfigAsync();
        if (config.SkipRenameSyncWarning != SkipWarningCheckbox)
        {
            config.SkipRenameSyncWarning = SkipWarningCheckbox;
            await _configService.SaveConfigAsync(config);
        }

        RenameErrorMessage = string.Empty;
        IsInputModalVisible = true;
    }

    /// <summary>
    /// Orchestrates global renaming of a file across the mesh.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmRename()
    {
        if (_fileToProcess == null || string.IsNullOrWhiteSpace(NewFileName) || !CanModify) return;

        RenameErrorMessage = string.Empty;
        string trimmedName = NewFileName.Trim();

        // Length validation against system constants.
        if (trimmedName.Length > AppConstants.MaxFileNameLength)
        {
            RenameErrorMessage = _localizer["Error_NameTooLong"];
            return;
        }

        string ext = Path.GetExtension(_fileToProcess.Name);
        string newFullName = $"{trimmedName}{ext}";

        if (_fileToProcess.Name == newFullName)
        {
            CancelRename(); // Centralized exit handles UI unlocking.
            return;
        }

        if (_allFetchedFiles.Any(f => f.Name.Equals(newFullName, StringComparison.OrdinalIgnoreCase)))
        {
            RenameErrorMessage = _localizer["Error_NameExists"];
            return;
        }

        IsRenaming = true;

        // Added timeout to rename operation
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            var config = await _configService.LoadConfigAsync();
            var master = config.Remotes.FirstOrDefault(r => r.FolderId == config.MasterRemoteId);

            if (master != null)
            {
                // Propagate security token to prevent UI hanging during background operations.
                await _renameService.RenameConversationAsync(_fileToProcess.Name, newFullName, master, config.Remotes,
                    timeoutCts.Token);

                var updatedItem = _fileToProcess with { Name = newFullName, ModTime = DateTime.UtcNow };
                int allIdx = _allFetchedFiles.IndexOf(_fileToProcess);
                if (allIdx >= 0) _allFetchedFiles[allIdx] = updatedItem;

                int filesIdx = Files.IndexOf(_fileToProcess);
                if (filesIdx >= 0) Files[filesIdx] = updatedItem;

                StatusMessage = _localizer["Success_NameUpdated"];
                IsInputModalVisible = false;
            }
        }
        catch (PartialRenameException partialEx)
        {
            // ARCHITECTURE: The rename succeeded on Master, but failed on some Slaves.
            // We MUST update the UI table anyway, because the file has physically changed on the Master drive.
            var updatedItem = _fileToProcess with { Name = newFullName, ModTime = DateTime.UtcNow };
            int allIdx = _allFetchedFiles.IndexOf(_fileToProcess);
            if (allIdx >= 0) _allFetchedFiles[allIdx] = updatedItem;

            int filesIdx = Files.IndexOf(_fileToProcess);
            if (filesIdx >= 0) Files[filesIdx] = updatedItem;

            // Warn the user about the impending duplicate after next sync
            StatusMessage = string.Format(_localizer["Error_PartialRename"], string.Join(", ", partialEx.FailedRemotes));
            Logger.Warn("Partial rename occurred. Desynchronization on: {0}", string.Join(", ", partialEx.FailedRemotes));
            IsInputModalVisible = false;
        }
        catch (OperationCanceledException)
        {
            // Timeout response
            RenameErrorMessage = _localizer["Error_OperationTimeout"];
            Logger.Error("Rename operation timed out after 2 minutes.");
        }
        catch (InvalidOperationException invEx)
        {
            RenameErrorMessage = invEx.Message;
        }
        catch (Exception ex)
        {
            RenameErrorMessage = _localizer["Error_ServerCommunication"];
            Logger.Error(ex, "Rename operation failed.");
        }
        finally
        {
            _fileToProcess = null;
            IsRenaming = false;
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
        }
    }
}