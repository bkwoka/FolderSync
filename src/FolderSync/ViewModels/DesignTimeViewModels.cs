using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using FolderSync.Services.SyncStages;

namespace FolderSync.ViewModels;

/// <summary>
/// Static class providing rich mock data for the XAML Previewer (Design-Time).
/// This allows for UI development without a running backend or active I/O.
/// </summary>
public static class DesignTimeViewModels
{
    public static SyncViewModel SyncViewModel { get; }
    public static SettingsViewModel SettingsViewModel { get; }
    public static BrowserViewModel BrowserViewModel { get; }
    public static MainWindowViewModel MainWindowViewModel { get; }

    static DesignTimeViewModels()
    {
        var dummyConfig = new DummyConfigService();
        var dummyRclone = new DummyRcloneService();
        var dummyGoogleApi = new DummyGoogleDriveApiService();
        var dummyMesh = new DummyMeshPermissionService();
        var dummyRename = new DummyRenameOrchestratorService();
        var dummyDelete = new DummyDeleteOrchestratorService();
        var dummyBootstrapper = new DummyRcloneBootstrapper();
        var localizer = TranslationService.Instance;

        var dummySanitize = new SyncSanitizeStage(dummyRclone, localizer);
        var dummyConsolidate = new SyncConsolidateStage(dummyRclone, localizer);
        var dummyCrossAccount = new SyncCrossAccountStage(dummyRclone, localizer);
        var dummySync = new SyncEngine(dummySanitize, dummyConsolidate, dummyCrossAccount, localizer);

        var dummyUpdate = new DummyUpdateService();
        var dummyFilePicker = new DummyFilePickerService();
        var dummyCrypto = new DummyProfileCryptoService();

        var dummyDriveOrchestrator = new DriveOrchestratorService(dummyConfig, dummyRclone, dummyGoogleApi, dummyMesh);
        var dummyProfileOrchestrator = new ProfileOrchestratorService(dummyCrypto, dummyConfig);

        // =========================================================
        // 1. MOCK: SYNC VIEW (SyncView)
        // =========================================================
        SyncViewModel = new SyncViewModel(dummySync, dummyConfig, dummyRclone, localizer)
        {
            Status = "Sync in progress (File 4 of 12)",
            ProgressValue = 65.5,
            IsBusy = true,
            SyncButtonText = "Syncing",
            IsValidationModalVisible = false
        };

        // Mock data
        SyncViewModel.AddLog(
            new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(), "Sanitization and Consolidation", false));
        SyncViewModel.AddLog(new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(),
            "    G_Drive_1: Sanitizing conversation names", false));
        SyncViewModel.AddLog(new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(),
            "    G_Drive_2: Sanitizing conversation names", false));
        SyncViewModel.AddLog(new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(),
            "    G_Drive_1: Fixing name duplicates (2)", false));
        SyncViewModel.AddLog(
            new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(), "    G_Drive_1: Consolidation", false));
        SyncViewModel.AddLog(
            new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(), "    G_Drive_2: Consolidation", false));
        SyncViewModel.AddLog(new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(),
            "    G_Drive_1: Moving contents of redundant folder 1A2B3C", false));
        SyncViewModel.AddLog(new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(),
            "    G_Drive_1: Deep analysis of 'Important project.prompt'", false));
        SyncViewModel.AddLog(new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(),
            "\nUpdating conversations across accounts\n", false));
        SyncViewModel.AddLog(new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(),
            "  ⇄ Aggregation: Checking for newer conversations on secondary drives", false));
        SyncViewModel.AddLog(new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(),
            "    ⬇ Downloading 3 newer conversations from G_Drive_2", false));
        SyncViewModel.AddLog(new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(),
            "\n  ⇄ Distribution: Broadcasting updated conversations to other drives", false));
        SyncViewModel.AddLog(new FolderSync.Helpers.SyncProgressEvent(Guid.NewGuid(),
            "    ⬆ Uploading 5 newest conversations to G_Drive_1", false));

        // =========================================================
        // 2. MOCK: SETTINGS VIEW (SettingsView)
        // =========================================================
        SettingsViewModel = new SettingsViewModel(dummyConfig, localizer, dummyBootstrapper, dummyFilePicker,
            dummyUpdate, dummyDriveOrchestrator, dummyProfileOrchestrator)
        {
            StatusMessage = "Ready to work",
            CurrentMasterName = "Master_BK (remote_master)",
            OauthCountdownText = "Waiting for browser... 04:23",

            IsUpdateAvailable = true,
            UpdateMessage = "🌟 New version available (v1.1.0)! Network stability improved.",
            AppVersionDisplay = "1.0.0",
        };

        if (SettingsViewModel.AvailableLanguages.Count > 1)
            SettingsViewModel.SelectedLanguage = SettingsViewModel.AvailableLanguages[1];

        var masterDrive =
            new RemoteInfo("Master_BK", "remote_master", "195h96kTLGZBL4-eE02REf4kqow4fn2u6", "master@domain.com")
                { IsMaster = true };
        var slaveDrive1 =
            new RemoteInfo("Telefon S23", "remote_phone", "285h77kPLGZAL4-xX01REf4kqow4fn3p9", "phone@domain.com")
                { IsMaster = false };
        var slaveDrive2 =
            new RemoteInfo("Konto Firmowe", "remote_work", "399x11kPLGZAL4-yY09REf4kqow4fn1v8", "work@company.com")
                { IsMaster = false };

        SettingsViewModel.SavedRemotes.Add(masterDrive);
        SettingsViewModel.SavedRemotes.Add(slaveDrive1);
        SettingsViewModel.SavedRemotes.Add(slaveDrive2);

        // =========================================================
        // 3. MOCK: BROWSER VIEW (BrowserView)
        // =========================================================
        BrowserViewModel = new BrowserViewModel(dummyRclone, dummyConfig, dummyRename, dummyDelete, localizer)
        {
            StatusMessage = "Displaying 4 results (28 files total).",
            CurrentDriveInfo = "Master_BK (master@domain.com)",
            IsLoading = false,
            ShowOnlyConversations = false,
            CanModify = true,
            SelectedRemote = masterDrive
        };

        BrowserViewModel.AvailableRemotes.Add(masterDrive);
        BrowserViewModel.AvailableRemotes.Add(slaveDrive1);
        BrowserViewModel.AvailableRemotes.Add(slaveDrive2);

        BrowserViewModel.Files.Add(new RcloneItem("1", "Project_Business_Logic.prompt", DateTime.Now.AddMinutes(-15),
            false, AppConstants.AiStudioMimeType));
        BrowserViewModel.Files.Add(new RcloneItem("2", "System_Architecture_v2.prompt", DateTime.Now.AddDays(-2),
            false, AppConstants.AiStudioMimeType));
        BrowserViewModel.Files.Add(new RcloneItem("3", "Attachment_Network_Diagram.png", DateTime.Now.AddDays(-3), false,
            "image/png"));
        BrowserViewModel.Files.Add(new RcloneItem("4", "API_Technical_Documentation.pdf", DateTime.Now.AddDays(-10),
            false, "application/pdf"));

        // =========================================================
        // 4. MOCK: MAIN WINDOW (MainWindow)
        // =========================================================
        MainWindowViewModel = new MainWindowViewModel(SyncViewModel, SettingsViewModel, BrowserViewModel,
            dummyBootstrapper, localizer)
        {
            IsRcloneMissing = false
        };
    }

    #region Dummy Services

    private class DummyRcloneBootstrapper : IRcloneBootstrapper
    {
        public bool IsInstalled() => true;
        public string GetExecutablePath() => "rclone";

        public Task InstallAsync(Action<double> progressCallback, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private class DummyConfigService : IConfigService
    {
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(new AppConfig
            { Remotes = new List<RemoteInfo>(), MasterRemoteId = null });

        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
    }

    private class DummyRcloneService : IRcloneService
    {
        public Task<string> ExecuteCommandAsync(string[] arguments, IEnumerable<string>? inputLines = null,
            CancellationToken cancellationToken = default, TimeSpan? timeout = null) => Task.FromResult(string.Empty);

        public Task<List<RcloneItem>> ListItemsAsync(string path, bool dirsOnly = false,
            CancellationToken cancellationToken = default) => Task.FromResult(new List<RcloneItem>());

        public Task<string> AuthorizeGoogleDrive(CancellationToken cancellationToken) => Task.FromResult("dummy_token");

        public Task CreateRemote(string name, string tokenJson, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> GetAccessTokenAsync(string rcloneRemote, CancellationToken cancellationToken = default) =>
            Task.FromResult("dummy_access_token");

        public Task DeleteRemoteAsync(string remoteName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> ReadFileContentAsync(string rcloneRemote, string folderId, string fileName,
            CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

        public Task<List<string>> GetConfiguredRemotesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<string>());
    }

    private class DummyGoogleDriveApiService : IGoogleDriveApiService
    {
        public Task<(string Name, string Email)> GetGoogleUserInfoAsync(string tokenJson,
            CancellationToken cancellationToken = default) => Task.FromResult(("Design User", "design@example.com"));

        public Task ShareFolderAsync(string rcloneRemote, string folderId, string targetEmail,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RevokePermissionAsync(string rcloneRemote, string folderId, string targetEmail,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TrashFileAsync(string rcloneRemote, string fileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> VerifyFolderExistsAsync(string tokenJson, string folderId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private class DummyMeshPermissionService : IMeshPermissionService
    {
        public Task GrantMeshPermissionsAsync(RemoteInfo newRemote, List<RemoteInfo> existingRemotes,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RevokeMeshPermissionsAsync(RemoteInfo targetToRemove, List<RemoteInfo> existingRemotes,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class DummyRenameOrchestratorService : IRenameOrchestratorService
    {
        public Task RenameConversationAsync(string oldFullName, string newFullName, RemoteInfo masterRemote,
            List<RemoteInfo> allRemotes, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class DummyDeleteOrchestratorService : IDeleteOrchestratorService
    {
        public Task DeleteConversationAsync(string fileName, bool deleteAttachments, RemoteInfo masterRemote,
            List<RemoteInfo> allRemotes, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class DummyFilePickerService : IFilePickerService
    {
        public Task<string?> SaveFileDialogAsync(string t, string s, string e) => Task.FromResult<string?>(null);
        public Task<string?> OpenFileDialogAsync(string t, string[] e) => Task.FromResult<string?>(null);
    }

    private class DummyProfileCryptoService : IProfileCryptoService
    {
        public Task ExportEncryptedProfileAsync(string p, string o) => Task.CompletedTask;
        public Task ImportEncryptedProfileAsync(string p, string i) => Task.CompletedTask;
    }

    private class DummyUpdateService : IUpdateService
    {
        public Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<UpdateInfo?>(new UpdateInfo("v1.2.0", "", true));
    }

    #endregion
}