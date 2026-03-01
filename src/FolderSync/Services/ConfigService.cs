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
            string json = await File.ReadAllTextAsync(_configPath).ConfigureAwait(false);
            return JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig) ?? CreateEmptyConfig();
        }
        catch (FileNotFoundException)
        {
            return CreateEmptyConfig();
        }
        catch (DirectoryNotFoundException)
        {
            return CreateEmptyConfig();
        }
        catch (JsonException jex)
        {
            Logger.Error(jex, "Config file is corrupted. Backing up and starting fresh.");
            try
            {
                string corruptedBackup = $"{_configPath}.corrupted_{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(_configPath, corruptedBackup, true);
            }
            catch (Exception copyEx)
            {
                Logger.Error(copyEx, "Failed to backup corrupted config.");
            }

            return CreateEmptyConfig();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "An unexpected error occurred while loading configuration.");
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