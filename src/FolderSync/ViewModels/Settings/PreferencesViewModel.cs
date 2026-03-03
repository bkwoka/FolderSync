using System;
using System.Collections.Generic;
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

public record LanguageItem(string DisplayName, string CultureCode);

/// <summary>
/// ViewModel responsible for application preferences (language, warnings) and update checking.
/// </summary>
public partial class PreferencesViewModel : ViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IConfigService _configService;
    private readonly IUpdateService _updateService;
    private readonly ITranslationService _localizer;

    /// <summary>
    /// Displays the current application version retrieved from the assembly metadata.
    /// </summary>
    [ObservableProperty]
    private string _appVersionDisplay = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "Unknown";

    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string _updateMessage = string.Empty;
    [ObservableProperty] private string _updateUrl = string.Empty;

    /// <summary>
    /// Controls the visibility of the manual update notification modal.
    /// </summary>
    [ObservableProperty] private bool _isUpdateModalVisible;

    [ObservableProperty] private bool _skipRenameWarningPreference;
    [ObservableProperty] private bool _skipDeleteWarningPreference;
    
    [ObservableProperty] private LanguageItem? _selectedLanguage;

    public List<LanguageItem> AvailableLanguages { get; } = new()
    {
        new LanguageItem("English", "en"),
        new LanguageItem("Polski", "pl")
    };

    private bool _isInitializing;

    public event Action<string>? StatusMessageChanged;

    public PreferencesViewModel(
        IConfigService configService,
        IUpdateService updateService,
        ITranslationService localizer)
    {
        _configService = configService;
        _updateService = updateService;
        _localizer = localizer;
    }

    public async Task LoadPreferencesAsync()
    {
        _ = CheckForUpdatesAsync();
        _isInitializing = true;
        try
        {
            var config = await _configService.LoadConfigAsync();
            SkipRenameWarningPreference = config.SkipRenameSyncWarning;
            SkipDeleteWarningPreference = config.SkipDeleteSyncWarning;
            SelectedLanguage = AvailableLanguages.FirstOrDefault(l => l.CultureCode == config.Language) ??
                               AvailableLanguages[0];
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load preferences.");
        }
        finally
        {
            _isInitializing = false;
        }
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
            StatusMessageChanged?.Invoke(_localizer["Error_CheckLogs"]);
            return false;
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
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = UpdateUrl, UseShellExecute = true });
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                System.Diagnostics.Process.Start("open", UpdateUrl);
            else
                System.Diagnostics.Process.Start("xdg-open", UpdateUrl);
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
