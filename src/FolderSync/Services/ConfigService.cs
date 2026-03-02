using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services;

/// <summary>
/// Source generation context for application configuration JSON serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppConfig))]
internal partial class AppConfigJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Service responsible for persistent application configuration management.
/// Implements thread-safe and resilient file operations.
/// </summary>
public class ConfigService : IConfigService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly string _configPath;

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public ConfigService()
    {
        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppConstants.AppDataFolderName);
        if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
        _configPath = Path.Combine(baseDir, AppConstants.ConfigFileName);
    }

    public async Task<AppConfig> LoadConfigAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Implement a retry strategy for transient I/O faults (e.g., file locked by antivirus).
            // We attempt to read the configuration 3 times with a 500ms delay before escalating.
            int retries = 3;
            while (true)
            {
                try
                {
                    string json = await File.ReadAllTextAsync(_configPath).ConfigureAwait(false);
                    return JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig) ?? CreateEmptyConfig();
                }
                catch (IOException ioEx)
                {
                    if (retries <= 0)
                    {
                        Logger.Error(ioEx, "Configuration file is permanently locked or inaccessible. Aborting to prevent profile loss.");
                        throw; 
                    }
                    
                    Logger.Warn(ioEx, "Configuration file is currently locked. Retrying in 500ms... ({0} retries left)", retries);
                    await Task.Delay(500).ConfigureAwait(false);
                    retries--;
                }
            }
        }
        catch (FileNotFoundException)
        {
            // Expected for new installations; return a default empty configuration.
            return CreateEmptyConfig();
        }
        catch (DirectoryNotFoundException)
        {
            // Expected for new installations; return a default empty configuration.
            return CreateEmptyConfig();
        }
        catch (JsonException jex)
        {
            Logger.Error(jex, "Configuration file is corrupted. Creating a backup and reinitializing with default settings.");
            try
            {
                string corruptedBackup = $"{_configPath}.corrupted_{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(_configPath, corruptedBackup, true);
            }
            catch (Exception copyEx)
            {
                Logger.Error(copyEx, "Secondary failure while attempting to backup the corrupted configuration.");
            }

            return CreateEmptyConfig();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveConfigAsync(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string tempPath = _configPath + ".tmp";

        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
            File.Move(tempPath, _configPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save configuration.");
            throw;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }

            _fileLock.Release();
        }
    }

    private AppConfig CreateEmptyConfig() => new() { MasterRemoteId = null, Remotes = [] };
}