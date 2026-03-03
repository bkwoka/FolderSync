using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FolderSync.Exceptions;
using FolderSync.Messages;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.ViewModels.Dialogs;

/// <summary>
/// ViewModel responsible exclusively for the conversation deletion flow,
/// including attachment management and orchestrator delegation.
/// </summary>
public partial class DeleteDialogViewModel : ViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IDeleteOrchestratorService _deleteService;
    private readonly IConfigService _configService;
    private readonly ITranslationService _localizer;

    [ObservableProperty] private bool _isDeleteModalVisible;
    [ObservableProperty] private bool _isDeleting;
    [ObservableProperty] private bool _deleteAttachmentsToggle;
    [ObservableProperty] private bool _showDeleteSyncWarning;
    [ObservableProperty] private bool _skipDeleteWarningCheckbox;
    [ObservableProperty] private string _deleteTargetName = string.Empty;

    private RcloneItem? _fileToProcess;

    public event Action<RcloneItem>? OnDeleteSuccess;
    public event Action<RcloneItem, List<string>>? OnPartialDelete;
    public event Action<string>? OnStatusMessage;

    public DeleteDialogViewModel(
        IDeleteOrchestratorService deleteService, 
        IConfigService configService, 
        ITranslationService localizer)
    {
        _deleteService = deleteService;
        _configService = configService;
        _localizer = localizer;
    }

    /// <summary>
    /// Initializes and displays the delete dialog flow for a specific file.
    /// </summary>
    public async Task StartAsync(RcloneItem item)
    {
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
        if (_fileToProcess == null)
        {
            // Failsafe UI release if state is inconsistent.
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
            return;
        }

        IsDeleting = true;
        OnStatusMessage?.Invoke(_localizer["Status_DeletingMesh"]);

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
                await _deleteService.DeleteConversationAsync(_fileToProcess.Name, DeleteAttachmentsToggle, master, config.Remotes, timeoutCts.Token);

                IsDeleteModalVisible = false;
                OnDeleteSuccess?.Invoke(_fileToProcess);
            }
        }
        catch (PartialDeletionException partialEx)
        {
            IsDeleteModalVisible = false;
            OnPartialDelete?.Invoke(_fileToProcess, partialEx.FailedRemotes);
        }
        catch (OperationCanceledException)
        {
            OnStatusMessage?.Invoke(_localizer["Error_OperationTimeout"]);
            Logger.Error("Delete operation timed out after 2 minutes.");
        }
        catch (Exception ex)
        {
            OnStatusMessage?.Invoke(_localizer["Error_DeleteFailed"]);
            Logger.Error(ex, "Delete operation failed.");
        }
        finally
        {
            if (!IsDeleteModalVisible)
            {
                _fileToProcess = null;
                WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
            }
            IsDeleting = false;
        }
    }
}
