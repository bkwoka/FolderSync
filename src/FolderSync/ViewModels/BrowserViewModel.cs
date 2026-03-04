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
using FolderSync.Messages;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using FolderSync.ViewModels.Dialogs;
using NLog;

namespace FolderSync.ViewModels;

/// <summary>
/// ViewModel for the File Browser view, allowing users to browse conversation files
/// across multiple configured Google Drive remotes. Delegates complex dialog flows to sub-ViewModels.
/// </summary>
public partial class BrowserViewModel : ViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IRcloneService _rcloneService;
    private readonly IConfigService _configService;
    private readonly ITranslationService _localizer;

    public RenameDialogViewModel RenameDialog { get; }
    public DeleteDialogViewModel DeleteDialog { get; }

    [ObservableProperty] private ObservableCollection<RcloneItem> _files = new();
    private List<RcloneItem> _allFetchedFiles = new();

    public ObservableCollection<RemoteInfo> AvailableRemotes { get; } = new();

    [ObservableProperty] private RemoteInfo? _selectedRemote;

    [ObservableProperty] private bool _canModify;

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    [ObservableProperty] private string _statusMessage;
    [ObservableProperty] private string _currentDriveInfo;
    [ObservableProperty] private bool _showOnlyConversations = true;

    private string? _lastViewedFolderId;

    // File loading timeout control
    private CancellationTokenSource? _refreshCts;

    public BrowserViewModel(
        IRcloneService rcloneService, 
        IConfigService configService,
        RenameDialogViewModel renameDialog,
        DeleteDialogViewModel deleteDialog,
        ITranslationService localizer)
    {
        _rcloneService = rcloneService;
        _configService = configService;
        RenameDialog = renameDialog;
        DeleteDialog = deleteDialog;
        _localizer = localizer;

        _statusMessage = _localizer["Status_LoadingInfo"];
        _currentDriveInfo = _localizer["Status_Checking"];

        WireUpDialogEvents();

        // Refresh UI state and filter when language changes
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (r, m) =>
        {
            if (!IsLoading && !RenameDialog.IsRenaming && !DeleteDialog.IsDeleting)
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

    private void WireUpDialogEvents()
    {
        RenameDialog.OnStatusMessage += msg => StatusMessage = msg;
        DeleteDialog.OnStatusMessage += msg => StatusMessage = msg;

        RenameDialog.OnRenameSuccess += updatedItem =>
        {
            UpdateItemInCollections(updatedItem);
            StatusMessage = _localizer["Success_NameUpdated"];
        };

        RenameDialog.OnPartialRename += (updatedItem, failedRemotes) =>
        {
            UpdateItemInCollections(updatedItem);
            StatusMessage = string.Format(_localizer["Error_PartialRename"], string.Join(", ", failedRemotes));
            Logger.Warn("Partial rename occurred. Desynchronization on: {0}", string.Join(", ", failedRemotes));
        };

        DeleteDialog.OnDeleteSuccess += deletedItem =>
        {
            _allFetchedFiles.Remove(deletedItem);
            Files.Remove(deletedItem);
            StatusMessage = _localizer["Success_ConversationDeleted"];
        };

        DeleteDialog.OnPartialDelete += (deletedItem, failedRemotes) =>
        {
            StatusMessage = string.Format(_localizer["Error_PartialDelete"], string.Join(", ", failedRemotes));
            Logger.Warn("Partial deletion occurred. Leftovers on: {0}", string.Join(", ", failedRemotes));
            
            // If the currently viewed remote was successful, remove it from the list anyway to reflect local state
            if (SelectedRemote != null && !failedRemotes.Contains(SelectedRemote.FriendlyName))
            {
                _allFetchedFiles.Remove(deletedItem);
                Files.Remove(deletedItem);
            }
        };
    }

    private void UpdateItemInCollections(RcloneItem updatedItem)
    {
        int allIdx = _allFetchedFiles.FindIndex(f => f.Id == updatedItem.Id);
        if (allIdx >= 0) _allFetchedFiles[allIdx] = updatedItem;

        int filesIdx = Files.ToList().FindIndex(f => f.Id == updatedItem.Id);
        if (filesIdx >= 0) Files[filesIdx] = updatedItem;
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

        try
        {
            var config = await _configService.LoadConfigAsync();

            // Synchronize the local AvailableRemotes collection with the current configuration state.
            var toRemove = AvailableRemotes.Where(ar => config.Remotes.All(cr => cr.FolderId != ar.FolderId)).ToList();
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
                // To maintain mesh consistency, structural modifications are restricted to the Master drive.
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
        catch (Exception ex)
        {
            // Gracefully handle configuration load failures to prevent crashing the main UI flow.
            Logger.Error(ex, "Failed to initialize or refresh the remote drive list from configuration.");
            StatusMessage = _localizer["Error_CheckLogs"];
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
        await DeleteDialog.StartAsync(item);
    }

    [RelayCommand]
    private async Task StartRename(RcloneItem item)
    {
        if (!CanModify) return;
        var existingNames = _allFetchedFiles.Select(f => f.Name);
        await RenameDialog.StartAsync(item, existingNames);
    }
}