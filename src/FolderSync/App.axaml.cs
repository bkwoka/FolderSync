using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using FolderSync.ViewModels;
using FolderSync.ViewModels.Settings;
using FolderSync.Views;
using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using System.Net.Http;
using System.Threading.Tasks;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using System.Text.RegularExpressions;
using NLog;

namespace FolderSync;

/// <summary>
/// Main Application class responsible for service registration and lifecycle management.
/// </summary>
public partial class App : Application
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Global access to the service provider.
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    [GeneratedRegex("rate_?limit_?exceeded", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitRegex();

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        PreloadLanguageSynchronously();

        var services = new ServiceCollection();

        // Configure resilient HTTP client with custom Google API resilience policy
        services.AddHttpClient(Options.DefaultName)
            .AddResilienceHandler("GoogleApiResilience", builder =>
            {
                builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
                {
                    // 6 attempts, which combined with exponential backoff provides enough time
                    // for the Google-side quota (Queries per minute) to reset.
                    MaxRetryAttempts = 6,
                    Delay = TimeSpan.FromSeconds(2),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = async args =>
                    {
                        // Retry on network errors (e.g., no internet, connection reset)
                        if (args.Outcome.Exception != null)
                        {
                            return true;
                        }

                        var response = args.Outcome.Result;
                        if (response == null) return false;

                        var statusCode = response.StatusCode;

                        // Persistent Google Server Errors and standard Rate Limits
                        if (statusCode == System.Net.HttpStatusCode.TooManyRequests || 
                            statusCode == System.Net.HttpStatusCode.RequestTimeout || 
                            (int)statusCode >= 500)
                        {
                            return true;
                        }

                        // Smart handling for 403 Forbidden errors.
                        // We distinguish between a temporary Quota Exceeded error (which merits a retry)
                        // and a permanent Access Denied error (which should fail-fast).
                        if (statusCode == System.Net.HttpStatusCode.Forbidden)
                        {
                            try
                            {
                                // Inspect the response body to determine the exact cause of the 403 error.
                                string content = await response.Content.ReadAsStringAsync();
                                
                                // Utilize the compiled Regex to identify Google's rate limit patterns.
                                if (RateLimitRegex().IsMatch(content))
                                {
                                    return true; // Quota reset required - trigger retry.
                                }
                            }
                            catch (Exception ex)
                            {
                                // Fail-Safe for I/O: Log failure to inspect the 403 response content.
                                // If the stream is unreadable, we abort retry attempts to prevent persistent hangs.
                                Logger.Warn(ex, "I/O failure while reading the 403 Forbidden response body. Aborting retry.");
                                return false;
                            }

                            // This is a standard permission denial (e.g., a specific account in our mesh 
                            // does not have access to a shared resource). Fail-fast to skip to the next account.
                            return false;
                        }

                        return false;
                    }
                });
            });

        // Infrastructure Services
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<ISecretVault, WindowsSecretVault>();
        }
        else
        {
            services.AddSingleton<ISecretVault, UnixMachineBoundVault>();
        }

        services.AddSingleton<ITokenCryptoService, TokenCryptoService>();
        
        services.AddSingleton<IRcloneBootstrapper, RcloneBootstrapper>();
        services.AddSingleton<IRcloneConfigManager, RcloneConfigManager>();
        services.AddSingleton<IRcloneProcessRunner, RcloneProcessRunner>();
        services.AddSingleton<IRcloneService, RcloneService>();
        services.AddSingleton<IGoogleDriveApiService, GoogleDriveApiService>();
        services.AddSingleton<IMeshPermissionService, MeshPermissionService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IProfileCryptoService, ProfileCryptoService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<ITranslationService>(TranslationService.Instance);
        services.AddSingleton<IPromptMetadataParser, PromptMetadataParser>();

        // Orchestrators and Business Logic
        services.AddSingleton<IRenameOrchestratorService, RenameOrchestratorService>();
        services.AddSingleton<IDeleteOrchestratorService, DeleteOrchestratorService>();
        services.AddSingleton<IDriveOrchestratorService, DriveOrchestratorService>();
        services.AddSingleton<IProfileOrchestratorService, ProfileOrchestratorService>();
        services.AddSingleton<ISyncEngine, SyncEngine>();

        // Sync Pipeline Stages
        services.AddSingleton<ISyncSanitizeStage, FolderSync.Services.SyncStages.SyncSanitizeStage>();
        services.AddSingleton<ISyncConsolidateStage, FolderSync.Services.SyncStages.SyncConsolidateStage>();
        services.AddSingleton<ISyncCrossAccountStage, FolderSync.Services.SyncStages.SyncCrossAccountStage>();

        // ViewModels - Registered as singletons to prevent memory leaks with Messenger subscriptions
        services.AddSingleton<SyncViewModel>();
        
        // Browser Sub-ViewModels
        services.AddSingleton<FolderSync.ViewModels.Dialogs.RenameDialogViewModel>();
        services.AddSingleton<FolderSync.ViewModels.Dialogs.DeleteDialogViewModel>();
        services.AddSingleton<BrowserViewModel>();
        
        services.AddSingleton<MainWindowViewModel>();

        // Settings Sub-ViewModels
        services.AddSingleton<DriveManagementViewModel>();
        services.AddSingleton<OAuthFlowViewModel>();
        services.AddSingleton<ProfileBackupViewModel>();
        services.AddSingleton<PreferencesViewModel>();
        services.AddSingleton<SettingsViewModel>();

        Services = services.BuildServiceProvider();

        // Initialize translation instance via DI
        var translationService = Services.GetRequiredService<ITranslationService>();
        TranslationService.SetInstance(translationService);

        // CRASH RESILIENCE: Clean up any orphaned temporary config files from previous abnormal terminations
        var configManager = Services.GetRequiredService<IRcloneConfigManager>();
        configManager.CleanupStaleTempConfigs();
        
        // MIGRATION: Ensure all legacy plaintext tokens are encrypted before the application starts
        Task.Run(() => configManager.MigratePlaintextTokensAsync()).Wait();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow { DataContext = Services.GetRequiredService<MainWindowViewModel>() };

            // Register application shutdown token for the IPC listener
            var cts = new System.Threading.CancellationTokenSource();
            desktop.ShutdownRequested += (s, e) => cts.Cancel();

            SingleInstanceManager.StartListening(desktop.MainWindow, cts.Token);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Preloads the application language before the full service container is built.
    /// </summary>
    private void PreloadLanguageSynchronously()
    {
        try
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FolderSync");
            string configPath = Path.Combine(baseDir, "appsettings.json");

            string lang = "en"; // Default fallback language if config is missing or invalid

            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                var config =
                    JsonSerializer.Deserialize(json, FolderSync.Services.AppConfigJsonContext.Default.AppConfig);

                if (config != null && !string.IsNullOrWhiteSpace(config.Language))
                {
                    lang = config.Language;
                }
            }

            TranslationService.Instance.Culture = new System.Globalization.CultureInfo(lang);
        }
        catch (Exception ex)
        {
            // Resilience: Log catastrophic failures during pre-boot configuration loading.
            // Reverting to default English culture to ensure the UI remains functional.
            Logger.Warn(ex, "Failed to preload language configuration. Falling back to English.");
            TranslationService.Instance.Culture = new System.Globalization.CultureInfo("en");
        }
    }

    /// <summary>
    /// Removes default Avalonia data annotation validation to allow custom MVVM validation.
    /// </summary>
    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}