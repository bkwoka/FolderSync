using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Services.Interfaces;
using FolderSync.Models;
using NLog;

namespace FolderSync.Services;

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

    /// <summary>
    /// Robustly parses an INI line to check if it represents the 'token' key, handling arbitrary whitespace.
    /// </summary>
    private static bool TryParseTokenLine(string line, out string prefix, out string value)
    {
        prefix = string.Empty;
        value = string.Empty;
        string trimmed = line.TrimStart();
        
        if (!trimmed.StartsWith("token", StringComparison.OrdinalIgnoreCase)) return false;
        
        int eqIndex = trimmed.IndexOf('=');
        if (eqIndex < 0) return false;

        string keyPart = trimmed.Substring(0, eqIndex).TrimEnd();
        if (!keyPart.Equals("token", StringComparison.OrdinalIgnoreCase)) return false;

        prefix = line.Substring(0, line.IndexOf('=') + 1);
        value = trimmed.Substring(eqIndex + 1).Trim();
        return true;
    }

    public async Task CreateRemoteAsync(string name, string tokenJson, CancellationToken cancellationToken = default)
    {
        string encryptedToken = _cryptoService.EncryptToken(tokenJson);

        await _configLock.WaitAsync(cancellationToken);
        try
        {
            Logger.Info("Creating new remote with at-rest encryption: {RemoteName}", name);
            string configPath = GetConfigPath();
            
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
                if (TryParseTokenLine(lines[i], out string prefix, out string tokenValue))
                {
                    if (tokenValue.StartsWith("enc:", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string decryptedToken = _cryptoService.DecryptToken(tokenValue);
                            lines[i] = $"{prefix} {decryptedToken}";
                            isModified = true;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Failed to decrypt token in config file at line {0}.", i + 1);
                            throw;
                        }
                    }
                }
            }

            string result = string.Join(Environment.NewLine, lines);
            if (isModified || lines.Length > 0) result += Environment.NewLine;
            
            return result;
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task UpdateTokenAsync(string remoteName, string newTokenJson, CancellationToken cancellationToken = default)
    {
        string encryptedToken = _cryptoService.EncryptToken(newTokenJson);
        
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            string path = GetConfigPath();
            if (!File.Exists(path)) return;

            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            bool inTargetRemote = false;
            bool updated = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inTargetRemote = trimmed.Equals($"[{remoteName}]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (inTargetRemote && TryParseTokenLine(lines[i], out string prefix, out _))
                {
                    lines[i] = $"{prefix} {encryptedToken}";
                    updated = true;
                    break;
                }
            }

            if (updated)
            {
                await File.WriteAllLinesAsync(path, lines, cancellationToken);
                Logger.Info("Successfully encrypted and saved refreshed token for remote: {0}", remoteName);
            }
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

            foreach (var line in lines)
            {
                if (TryParseTokenLine(line, out _, out string tokenValue) && !tokenValue.StartsWith("enc:", StringComparison.OrdinalIgnoreCase))
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
                    if (TryParseTokenLine(lines[i], out string prefix, out string tokenValue) && !tokenValue.StartsWith("enc:", StringComparison.OrdinalIgnoreCase))
                    {
                        string encryptedToken = _cryptoService.EncryptToken(tokenValue);
                        lines[i] = $"{prefix} {encryptedToken}";
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
