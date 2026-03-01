using System;
using System.IO;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace FolderSync.Services;

/// <summary>
/// Provides centralized configuration for the NLog logging framework.
/// Sets up file and debug targets with appropriate layouts and rotation policies.
/// </summary>
public static class LoggingConfig
{
    private const long MaxLogSize = 5 * 1024 * 1024; // 5 MB
    private const int MaxArchiveFiles = 3;

    /// <summary>
    /// Initializes the logging system with file and debug output rules.
    /// Logs are stored in the user's local application data directory.
    /// </summary>
    public static void Setup()
    {
        var config = new LoggingConfiguration();

        // Ensure logs directory exists in local application data
        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppConstants.AppDataFolderName, "log");
        if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);

        string logFilePath = Path.Combine(baseDir, "FolderSync.log");

        var fileTarget = new FileTarget("logfile")
        {
            FileName = logFilePath,
            Layout = "${longdate}|${level:uppercase=true}|${logger}|${message} ${exception:format=tostring}",
            Encoding = System.Text.Encoding.UTF8,
            KeepFileOpen = true,
            ConcurrentWrites = true,
            ArchiveFileName = Path.Combine(baseDir, "FolderSync.archive.{#}.log"),
            ArchiveNumbering = ArchiveNumberingMode.Rolling,
            ArchiveAboveSize = MaxLogSize,
            MaxArchiveFiles = MaxArchiveFiles,
        };

        var debugTarget = new DebugTarget("debuglog")
        {
            Layout = "${level:uppercase=true}: ${message}"
        };

#if DEBUG
        var minLogLevel = LogLevel.Debug;
#else
        var minLogLevel = LogLevel.Info;
#endif

        config.AddRule(minLogLevel, LogLevel.Fatal, fileTarget);
        config.AddRule(LogLevel.Debug, LogLevel.Fatal, debugTarget);

        LogManager.Configuration = config;
    }
}