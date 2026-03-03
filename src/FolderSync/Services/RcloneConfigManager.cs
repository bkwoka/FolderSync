using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Services.Interfaces;
using NLog;
using FolderSync.Models;

namespace FolderSync.Services;

/// <summary>
/// Manages the physical rclone.conf file. Ensures tokens are encrypted at-rest 
/// and provides in-memory decryption for runtime execution.
/// </summary>
public class RcloneConfigManager : IRcloneConfigManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly SemaphoreSlim _configLock = new(1, 1);
    
    private readonly ITokenCryptoService _cryptoService;

    public RcloneConfigManager(ITokenCryptoService cryptoService)
    {
        _cryptoService = cryptoService;
    }

    public string GetConfigPath()
    {
        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConstants.AppDataFolderName);
        if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, AppConstants.RcloneConfigFileName);
    }

    public async Task CreateRemoteAsync(string name, string tokenJson, CancellationToken cancellationToken = default)
    {
        // Encrypt the token before it ever touches the disk
        string encryptedToken = _cryptoService.EncryptToken(tokenJson);

        await _configLock.WaitAsync(cancellationToken);
        try
        {
            Logger.Info("Creating new remote with at-rest encryption: {RemoteName}", name);
            string configPath = GetConfigPath();
            
            // The INI section header requires square brackets.
            string configBlock = $"\n[{name}]\ntype = drive\nconfig_is_local = false\ntoken = {encryptedToken}\n";
            await File.AppendAllTextAsync(configPath, configBlock, cancellationToken);
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task<string> GetDecryptedConfigAsync(CancellationToken cancellationToken = default)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            string path = GetConfigPath();
            if (!File.Exists(path)) return string.Empty;

            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            bool isModified = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("token = enc:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Extract the encrypted payload (skip "token = ")
                        string encryptedToken = trimmed.Substring(8).Trim();
                        string decryptedToken = _cryptoService.DecryptToken(encryptedToken);

                        // Preserve original indentation
                        int indentLength = lines[i].Length - trimmed.Length;
                        string indent = lines[i].Substring(0, indentLength);
                        
                        lines[i] = $"{indent}token = {decryptedToken}";
                        isModified = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to decrypt token in config file at line {0}. The vault key might be missing or invalid.", i + 1);
                        throw;
                    }
                }
            }

            string result = string.Join(Environment.NewLine, lines);
            // Ensure trailing newline for valid INI format
            if (isModified || lines.Length > 0) result += Environment.NewLine;
            
            return result;
        }
        finally
        {
            _configLock.Release();
        }
    }

    public void CleanupStaleTempConfigs()
    {
        try
        {
            string dir = Path.GetDirectoryName(GetConfigPath())!;
            if (Directory.Exists(dir))
            {
                var staleFiles = Directory.GetFiles(dir, "temp_*.conf");
                foreach (var file in staleFiles)
                {
                    File.Delete(file);
                    Logger.Info("Cleaned up stale temporary config file: {0}", Path.GetFileName(file));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to clean up stale temporary config files.");
        }
    }

    public async Task MigratePlaintextTokensAsync()
    {
        string path = GetConfigPath();
        if (!File.Exists(path)) return;

        await _configLock.WaitAsync();
        try
        {
            var lines = await File.ReadAllLinesAsync(path);
            bool needsMigration = false;

            // Fast scan to determine if migration is needed
            foreach (var line in lines)
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("token = ", StringComparison.OrdinalIgnoreCase) && 
                    !trimmed.StartsWith("token = enc:", StringComparison.OrdinalIgnoreCase))
                {
                    needsMigration = true;
                    break;
                }
            }

            if (!needsMigration) return;

            Logger.Info("Legacy plaintext tokens detected in rclone.conf. Initiating migration to encrypted vault...");

            string backupPath = path + ".bak";
            File.Copy(path, backupPath, true);

            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("token = ", StringComparison.OrdinalIgnoreCase) && 
                        !trimmed.StartsWith("token = enc:", StringComparison.OrdinalIgnoreCase))
                    {
                        string plainToken = trimmed.Substring(8).Trim();
                        string encryptedToken = _cryptoService.EncryptToken(plainToken);
                        
                        int indentLength = lines[i].Length - trimmed.Length;
                        string indent = lines[i].Substring(0, indentLength);
                        
                        lines[i] = $"{indent}token = {encryptedToken}";
                    }
                }

                await File.WriteAllLinesAsync(path, lines);
                File.Delete(backupPath);
                Logger.Info("Migration to encrypted vault completed successfully.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Migration failed. Restoring backup to prevent data loss.");
                File.Copy(backupPath, path, true);
                throw;
            }
        }
        finally
        {
            _configLock.Release();
        }
    }
}
