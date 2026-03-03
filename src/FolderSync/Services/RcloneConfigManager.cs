using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services;

/// <summary>
/// Manages the physical rclone.conf file, ensuring thread-safe modifications.
/// </summary>
public class RcloneConfigManager : IRcloneConfigManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly SemaphoreSlim _configLock = new(1, 1);

    public string GetConfigPath()
    {
        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConstants.AppDataFolderName);
        if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, AppConstants.RcloneConfigFileName);
    }

    public async Task CreateRemoteAsync(string name, string tokenJson, CancellationToken cancellationToken = default)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            Logger.Info("Creating new remote directly via configuration file manipulation: {RemoteName}", name);
            string configPath = GetConfigPath();
            // The INI section header requires square brackets.
            string configBlock = $"\n[{name}]\ntype = drive\nconfig_is_local = false\ntoken = {tokenJson}\n";
            await File.AppendAllTextAsync(configPath, configBlock, cancellationToken);
        }
        finally
        {
            _configLock.Release();
        }
    }
}
