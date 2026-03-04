using System;
using System.Collections.Generic;
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
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.ViewModels.Dialogs;

/// <summary>
/// ViewModel responsible exclusively for the conversation renaming flow,
/// including validation, user warnings, and orchestrator delegation.
/// </summary>
public partial class RenameDialogViewModel : ViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IRenameOrchestratorService _renameService;
    private readonly IConfigService _configService;
    private readonly ITranslationService _localizer;

    [ObservableProperty] private bool _isWarningModalVisible;
    [ObservableProperty] private bool _isInputModalVisible;
    [ObservableProperty] private string _newFileName = string.Empty;
    [ObservableProperty] private bool _skipWarningCheckbox;
    [ObservableProperty] private bool _isRenaming;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(HasRenameError))]
    private string _renameErrorMessage = string.Empty;

    public bool HasRenameError => !string.IsNullOrEmpty(RenameErrorMessage);

    private RcloneItem? _fileToProcess;
    private IEnumerable<string> _existingFileNames = Enumerable.Empty<string>();

    public event Action<RcloneItem>? OnRenameSuccess;
    public event Action<RcloneItem, List<string>>? OnPartialRename;
    public event Action<string>? OnStatusMessage;

    public RenameDialogViewModel(
        IRenameOrchestratorService renameService, 
        IConfigService configService, 
        ITranslationService localizer)
    {
        _renameService = renameService;
        _configService = configService;
        _localizer = localizer;
    }

    /// <summary>
    /// Initializes and displays the rename dialog flow for a specific file.
    /// </summary>
    public async Task StartAsync(RcloneItem item, IEnumerable<string> existingFileNames)
    {
        // Secure UI State: Lock interface to ensure atomic rename operation lifecycle.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));

        _fileToProcess = item;
        _existingFileNames = existingFileNames;
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
        if (_fileToProcess == null || string.IsNullOrWhiteSpace(NewFileName)) return;

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

        if (_existingFileNames.Any(name => name.Equals(newFullName, StringComparison.OrdinalIgnoreCase)))
        {
            RenameErrorMessage = _localizer["Error_NameExists"];
            return;
        }

        IsRenaming = true;
        OnStatusMessage?.Invoke(_localizer["Status_CheckingCloud"]);

        // Added timeout to rename operation
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            var config = await _configService.LoadConfigAsync();
            var master = config.Remotes.FirstOrDefault(r => r.FolderId == config.MasterRemoteId);

            if (master != null)
            {
                // Propagate security token to prevent UI hanging during background operations.
                await _renameService.RenameConversationAsync(_fileToProcess.Name, newFullName, master, config.Remotes, timeoutCts.Token);

                var updatedItem = _fileToProcess with { Name = newFullName, ModTime = DateTime.UtcNow };
                
                IsInputModalVisible = false;
                OnRenameSuccess?.Invoke(updatedItem);
            }
        }
        catch (PartialRenameException partialEx)
        {
            var updatedItem = _fileToProcess with { Name = newFullName, ModTime = DateTime.UtcNow };
            IsInputModalVisible = false;
            OnPartialRename?.Invoke(updatedItem, partialEx.FailedRemotes);
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
            if (!IsInputModalVisible) 
            {
                _fileToProcess = null;
                WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
            }
            IsRenaming = false;
        }
    }
}
