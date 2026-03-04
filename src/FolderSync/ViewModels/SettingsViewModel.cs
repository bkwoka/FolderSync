using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FolderSync.Messages;
using FolderSync.Services.Interfaces;
using FolderSync.ViewModels.Settings;
using FolderSync.Services;

namespace FolderSync.ViewModels;

/// <summary>
/// Host ViewModel for the Settings view. Uses ViewModel Composition to delegate
/// responsibilities to smaller, highly cohesive sub-ViewModels.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    public DriveManagementViewModel DriveManagement { get; }
    public OAuthFlowViewModel OAuthFlow { get; }
    public ProfileBackupViewModel ProfileBackup { get; }
    public PreferencesViewModel Preferences { get; }

    [ObservableProperty] private string _statusMessage;

    public SettingsViewModel(
        DriveManagementViewModel driveManagement,
        OAuthFlowViewModel oauthFlow,
        ProfileBackupViewModel profileBackup,
        PreferencesViewModel preferences,
        ITranslationService localizer)
    {
        DriveManagement = driveManagement;
        OAuthFlow = oauthFlow;
        ProfileBackup = profileBackup;
        Preferences = preferences;

        _statusMessage = localizer["Status_Ready"];

        // Wire up status message updates from sub-ViewModels
        DriveManagement.StatusMessageChanged += msg => StatusMessage = msg;
        OAuthFlow.StatusMessageChanged += msg => StatusMessage = msg;
        ProfileBackup.StatusMessageChanged += msg => StatusMessage = msg;
        Preferences.StatusMessageChanged += msg => StatusMessage = msg;

        // Wire up cross-ViewModel communication
        OAuthFlow.DriveAdded += async () => await DriveManagement.LoadDataAndCheckIntegrityAsync();
        
        ProfileBackup.ProfileImported += async () => 
        {
            await DriveManagement.LoadDataAndCheckIntegrityAsync();
            await Preferences.LoadPreferencesAsync();
            
            if (Preferences.SelectedLanguage != null)
            {
                localizer.Culture = new System.Globalization.CultureInfo(Preferences.SelectedLanguage.CultureCode);
                WeakReferenceMessenger.Default.Send(new LanguageChangedMessage(Preferences.SelectedLanguage.CultureCode));
            }
        };

        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (r, m) =>
        {
            if (!IsAppLocked) StatusMessage = localizer["Status_Ready"];
            if (DriveManagement.IsIntegrityModalVisible)
                StatusMessage = localizer["Warning_IntegrityProblem"];
        });

        _ = InitializeAsync();
    }

    protected override void OnAppLockChanged(bool isLocked)
    {
        StatusMessage = isLocked ? TranslationService.Instance["Status_AppLockedSync"] : TranslationService.Instance["Status_Ready"];
    }

    private async Task InitializeAsync()
    {
        await DriveManagement.LoadDataAndCheckIntegrityAsync();
        await Preferences.LoadPreferencesAsync();
    }
}