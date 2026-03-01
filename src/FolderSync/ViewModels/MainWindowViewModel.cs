using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderSync.Services.Interfaces;

namespace FolderSync.ViewModels;

/// <summary>
/// Interaction logic for the main application window, managing navigation tabs 
/// and the initial Rclone environment bootstrap.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    public SyncViewModel SyncTab { get; }
    public SettingsViewModel SettingsTab { get; }
    public BrowserViewModel BrowserTab { get; }

    [ObservableProperty] private bool _isRcloneMissing;
    [ObservableProperty] private bool _isDownloadingRclone;
    [ObservableProperty] private double _rcloneDownloadProgress;
    [ObservableProperty] private string _downloadStatus = string.Empty;

    public MainWindowViewModel(SyncViewModel syncTab, SettingsViewModel settingsTab, BrowserViewModel browserTab,
        IRcloneBootstrapper bootstrapper, ITranslationService localizer)
    {
        SyncTab = syncTab;
        SettingsTab = settingsTab;
        BrowserTab = browserTab;

        _ = VerifyAndInstallRcloneAsync(bootstrapper, localizer);
    }

    /// <summary>
    /// Checks for Rclone installation and initiates the automatic bootstrapper if missing.
    /// </summary>
    private async Task VerifyAndInstallRcloneAsync(IRcloneBootstrapper bootstrapper, ITranslationService localizer)
    {
        if (bootstrapper.IsInstalled())
        {
            IsRcloneMissing = false;
            return;
        }

        IsRcloneMissing = true;
        IsDownloadingRclone = true;
        DownloadStatus = string.Format(localizer["Bootstrapper_Downloading"], AppConstants.RcloneTargetVersion);

        try
        {
            await bootstrapper.InstallAsync(progress =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => RcloneDownloadProgress = progress);
            });

            IsDownloadingRclone = false;
            IsRcloneMissing = false;
        }
        catch (Exception ex)
        {
            DownloadStatus = string.Format(localizer["Bootstrapper_Error"], ex.Message);
            IsDownloadingRclone = false;
        }
    }
}