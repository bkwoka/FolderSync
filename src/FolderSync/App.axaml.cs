using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using FolderSync.ViewModels;
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

namespace FolderSync;

/// <summary>
/// Main Application class responsible for service registration and lifecycle management.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Global access to the service provider.
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

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
                    ShouldHandle = args =>
                    {
                        // Retry on network errors (e.g., no internet, connection reset)
                        if (args.Outcome.Exception != null)
                        {
                            return new ValueTask<bool>(true);
                        }

                        var statusCode = args.Outcome.Result?.StatusCode;
                        
                        // Key logic: catch internal Google error codes, including the problematic 403 Forbidden.
                        bool shouldRetry = statusCode == System.Net.HttpStatusCode.Forbidden ||       // Google Rate Limit / Quota Exceeded
                                           statusCode == System.Net.HttpStatusCode.TooManyRequests || // Standard Rate Limit
                                           statusCode == System.Net.HttpStatusCode.RequestTimeout ||  // Timeout
                                           (int?)statusCode >= 500;                                   // Google Server Errors

                        return new ValueTask<bool>(shouldRetry);
                    }
                });
            });

        // Infrastructure Services
        services.AddSingleton<IRcloneBootstrapper, RcloneBootstrapper>();
        services.AddSingleton<IRcloneService, RcloneService>();
        services.AddSingleton<IGoogleDriveApiService, GoogleDriveApiService>();
        services.AddSingleton<IMeshPermissionService, MeshPermissionService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IProfileCryptoService, ProfileCryptoService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<ITranslationService>(TranslationService.Instance);

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
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<BrowserViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        Services = services.BuildServiceProvider();

        // Initialize translation instance via DI
        var translationService = Services.GetRequiredService<ITranslationService>();
        TranslationService.SetInstance(translationService);

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
        catch
        {
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