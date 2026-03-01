using Avalonia;
using System;
using FolderSync.Services;
using Fonts.Avalonia.JetBrainsMono;

namespace FolderSync;

/// <summary>
/// Entry point for the FolderSync application.
/// </summary>
sealed class Program
{
    /// <summary>
    /// Application entry point. Enforces a single-instance policy via IPC before initializing the UI framework.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    [STAThread]
    public static void Main(string[] args)
    {
        // Global logging configuration must be initialized before any other logic.
        LoggingConfig.Setup();

        // Single-instance guard: Check IPC before loading the UI engine to prevent duplicate processes.
        if (SingleInstanceManager.TrySendWakeUp())
        {
            NLog.LogManager.GetCurrentClassLogger()
                .Info("Application is already running. Wake-up signal sent. Aborting new instance startup.");
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Fatal(ex, "Application crashed during startup.");
        }
    }

    /// <summary>
    /// Configures the Avalonia application builder. Used by both the runtime and design-time tools.
    /// </summary>
    /// <returns>A configured <see cref="AppBuilder"/> instance.</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithJetBrainsMonoFont()
            .LogToTrace();
}