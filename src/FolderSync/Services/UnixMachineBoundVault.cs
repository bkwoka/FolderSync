using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using FolderSync.Exceptions;
using FolderSync.Services.Interfaces;
using FolderSync.Models;
using NLog;

namespace FolderSync.Services;

/// <summary>
/// Unix-specific fallback implementation of ISecretVault (Soft Protection).
/// Derives a deterministic encryption key using HKDF-SHA256 based on the OS machine-id and a local salt.
/// </summary>
[UnsupportedOSPlatform("windows")]
public partial class UnixMachineBoundVault : ISecretVault
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly string _storageDir;

    [GeneratedRegex(@"IOPlatformUUID""\s*=\s*""([a-zA-Z0-9-]+)""")]
    private static partial Regex MacUuidRegex();

    public UnixMachineBoundVault()
    {
        _storageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConstants.AppDataFolderName, "secrets");
        if (!Directory.Exists(_storageDir))
        {
            Directory.CreateDirectory(_storageDir);
            RestrictPermissions(_storageDir);
        }
        
        Logger.Info("UnixMachineBoundVault active - Soft Protection mode engaged.");
    }

    private string GetPath(string key) => Path.Combine(_storageDir, $"{key}.enc");
    private string GetMetaPath() => Path.Combine(_storageDir, "vault.meta");

    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                var info = new FileInfo(path);
                info.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to set strict Unix file permissions on {0}", path);
            }
        }
    }

    private string GetMachineId()
    {
        if (OperatingSystem.IsLinux())
        {
            if (File.Exists("/etc/machine-id")) return File.ReadAllText("/etc/machine-id").Trim();
            if (File.Exists("/var/lib/dbus/machine-id")) return File.ReadAllText("/var/lib/dbus/machine-id").Trim();
        }
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = "ioreg", Arguments = "-rd1 -c IOPlatformExpertDevice", RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    var match = MacUuidRegex().Match(output);
                    if (match.Success) return match.Groups[1].Value;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to retrieve macOS IOPlatformUUID.");
            }
        }

        throw new PlatformNotSupportedException("Unable to determine a stable hardware/machine ID for this Unix system.");
    }

    private byte[] DeriveKey()
    {
        string machineId = GetMachineId();
        string metaPath = GetMetaPath();

        // Generate and store a random salt on first run to prevent rainbow table attacks across different installations
        if (!File.Exists(metaPath))
        {
            byte[] newSalt = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(metaPath, newSalt);
            RestrictPermissions(metaPath);
        }

        byte[] salt = File.ReadAllBytes(metaPath);
        byte[] ikm = Encoding.UTF8.GetBytes(machineId);
        byte[] info = Encoding.UTF8.GetBytes("FolderSync.UnixVault.v1");

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, info);
    }

    public void StoreSecret(string key, byte[] secret)
    {
        byte[] derivedKey = DeriveKey();
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[secret.Length];

        using (var aes = new AesGcm(derivedKey, tag.Length))
        {
            aes.Encrypt(nonce, secret, ciphertext, tag);
        }

        // Format: NONCE(12) + TAG(16) + CIPHERTEXT
        using var ms = new MemoryStream();
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(ciphertext);

        string path = GetPath(key);
        File.WriteAllBytes(path, ms.ToArray());
        RestrictPermissions(path);
        
        Logger.Info("Secret '{0}' securely stored using Machine-Bound AES-GCM.", key);
    }

    public byte[]? RetrieveSecret(string key)
    {
        string path = GetPath(key);
        if (!File.Exists(path)) return null;

        byte[] derivedKey;
        try
        {
            derivedKey = DeriveKey();
        }
        catch (Exception ex)
        {
            throw new VaultKeyLostException("Failed to derive the machine key. The system identity might have changed.", ex);
        }

        byte[] fileBytes = File.ReadAllBytes(path);
        if (fileBytes.Length < 28) throw new CryptographicException("Vault file is corrupted (too short).");

        byte[] nonce = fileBytes.AsSpan(0, 12).ToArray();
        byte[] tag = fileBytes.AsSpan(12, 16).ToArray();
        byte[] ciphertext = fileBytes.AsSpan(28).ToArray();
        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(derivedKey, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            Logger.Error(ex, "Machine-Bound decryption failed. Hardware ID mismatch or corrupted vault.");
            throw new VaultKeyLostException("Failed to decrypt the vault. The machine identity has changed. Please restore your profile from a .fsbak backup.", ex);
        }
    }

    public bool HasSecret(string key) => File.Exists(GetPath(key));

    public void DeleteSecret(string key)
    {
        string path = GetPath(key);
        if (File.Exists(path)) File.Delete(path);
    }
}
