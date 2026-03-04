using System;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using FolderSync.Services.Interfaces;
using FolderSync.Models;
using NLog;

namespace FolderSync.Services;

/// <summary>
/// Windows-specific implementation of ISecretVault using the Data Protection API (DPAPI).
/// Cryptographically binds the secret to the current Windows user account.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsSecretVault : ISecretVault
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly string _storageDir;

    public WindowsSecretVault()
    {
        _storageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConstants.AppDataFolderName, "secrets");
        if (!Directory.Exists(_storageDir)) Directory.CreateDirectory(_storageDir);
    }

    private string GetPath(string key) => Path.Combine(_storageDir, $"{key}.dat");

    public void StoreSecret(string key, byte[] secret)
    {
        try
        {
            // DPAPI encryption scoped to the current user. No additional entropy needed as the OS handles the master key.
            byte[] protectedBytes = ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(GetPath(key), protectedBytes);
            Logger.Info("Secret '{0}' securely stored using Windows DPAPI.", key);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to store secret using DPAPI.");
            throw;
        }
    }

    public byte[]? RetrieveSecret(string key)
    {
        string path = GetPath(key);
        if (!File.Exists(path)) return null;

        try
        {
            byte[] protectedBytes = File.ReadAllBytes(path);
            return ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            Logger.Error(ex, "DPAPI decryption failed. The user account might have changed or the file is corrupted.");
            throw new Exceptions.VaultKeyLostException("Windows Credential Manager failed to decrypt the vault. Please restore your profile from a .fsbak backup.", ex);
        }
    }

    public bool HasSecret(string key) => File.Exists(GetPath(key));

    public void DeleteSecret(string key)
    {
        string path = GetPath(key);
        if (File.Exists(path)) File.Delete(path);
    }
}
