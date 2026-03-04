using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FolderSync.Messages;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.ViewModels.Settings;

/// <summary>
/// ViewModel responsible for the Google Drive OAuth authorization flow,
/// folder auto-detection, and adding new remotes to the mesh.
/// </summary>
public partial class OAuthFlowViewModel : ViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IDriveOrchestratorService _driveOrchestrator;
    private readonly IConfigService _configService;
    private readonly ITranslationService _localizer;

    [ObservableProperty] private string _pendingName = string.Empty;
    [ObservableProperty] private string _pendingEmail = string.Empty;
    [ObservableProperty] private string _pendingFolderId = string.Empty;
    private string _pendingToken = string.Empty;

    [ObservableProperty] private string _oauthCountdownText = string.Empty;

    [ObservableProperty] private bool _isOauthModalVisible;
    [ObservableProperty] private bool _isConfigModalVisible;
    [ObservableProperty] private bool _isOverwriteModalVisible;
    [ObservableProperty] private bool _isVerifyingFolder;

    /// <summary>
    /// Indicates whether the system is currently scanning Google Drive for an existing "Google AI Studio" folder.
    /// </summary>
    [ObservableProperty] private bool _isAutoDetectingFolder;

    /// <summary>
    /// Tracks drive configuration save state to prevent concurrent modifications within the config modal.
    /// </summary>
    [ObservableProperty] private bool _isSavingDrive;

    private CancellationTokenSource? _oauthCts;

    public event Action<string>? StatusMessageChanged;
    public event Func<Task>? DriveAdded;

    public OAuthFlowViewModel(
        IDriveOrchestratorService driveOrchestrator,
        IConfigService configService,
        ITranslationService localizer)
    {
        _driveOrchestrator = driveOrchestrator;
        _configService = configService;
        _localizer = localizer;
    }

    protected override void OnAppLockChanged(bool isLocked)
    {
        StartAddDriveCommand.NotifyCanExecuteChanged();
    }

    private bool CanAddDrive() => !IsAppLocked;

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
            StatusMessageChanged?.Invoke(_localizer["Status_LoginCancelled"]);
            IsOauthModalVisible = false;
            // Unlock UI if the authorization process is explicitly cancelled.
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(_localizer["Error_GoogleAuth"]);
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
        StatusMessageChanged?.Invoke(_localizer["Status_DriveAddCancelled"]);
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
        StatusMessageChanged?.Invoke(_localizer["Status_DriveOverwriteCancelled"]);
        // Restore UI availability after cancelling overwrite operation.
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
    }

    [RelayCommand]
    private async Task ConfirmConfigAsync()
    {
        if (string.IsNullOrWhiteSpace(PendingName))
        {
            StatusMessageChanged?.Invoke(_localizer["Error_NameRequired"]);
            return;
        }
        
        bool isNameSafe = PendingName.All(c => char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-');
        if (!isNameSafe)
        {
            StatusMessageChanged?.Invoke(_localizer["Error_InvalidCharacters"]);
            return;
        }

        if (string.IsNullOrWhiteSpace(PendingFolderId))
        {
            StatusMessageChanged?.Invoke(_localizer["Error_FolderIdRequired"]);
            return;
        }

        IsVerifyingFolder = true;
        try
        {
            bool folderExists = await _driveOrchestrator.VerifyFolderExistsAsync(_pendingToken, PendingFolderId);
            if (!folderExists)
            {
                StatusMessageChanged?.Invoke(_localizer["Error_FolderNotFound"]);
                return;
            }
        }
        finally
        {
            IsVerifyingFolder = false;
        }

        var config = await _configService.LoadConfigAsync();
        bool emailExists = config.Remotes.Any(r => r.RcloneRemote.Equals(PendingEmail, StringComparison.OrdinalIgnoreCase));
        
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
        StatusMessageChanged?.Invoke(_localizer["Status_SavingMesh"]);
        WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(true));
        
        try
        {
            await _driveOrchestrator.AddNewDriveAsync(PendingName, PendingEmail, PendingFolderId, _pendingToken, overwrite);
            
            if (DriveAdded != null)
            {
                await DriveAdded.Invoke();
            }

            StatusMessageChanged?.Invoke(_localizer["Success_DriveAdded"]);
            
            // Successfully integrated: Close any open configuration or overwrite modals.
            IsConfigModalVisible = false;
            IsOverwriteModalVisible = false;
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(_localizer["Error_DriveSaveFailed"]);
            Logger.Error(ex, "FinalizeAddDrive failed");
        }
        finally
        {
            IsSavingDrive = false;
            WeakReferenceMessenger.Default.Send(new SyncStateChangedMessage(false));
        }
    }
}
