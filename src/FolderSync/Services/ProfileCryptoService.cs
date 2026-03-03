using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FolderSync.Services.Interfaces;
using FolderSync.Models;

namespace FolderSync.Services;

/// <summary>
/// Service providing secure backup and restore functionality for application profiles.
/// Uses AES-GCM for authenticated encryption and PBKDF2 for key derivation.
/// </summary>
public class ProfileCryptoService : IProfileCryptoService
{
    private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("FSBAK");
    private const int MaxBackupSize = 50 * 1024 * 1024; // 50MB safety limit

    private readonly IRcloneConfigManager _configManager;
    private readonly ITokenCryptoService _tokenCryptoService;

    public ProfileCryptoService(IRcloneConfigManager configManager, ITokenCryptoService tokenCryptoService)
    {
        _configManager = configManager;
        _tokenCryptoService = tokenCryptoService;
    }

    private string GetConfigPath()
    {
        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConstants.AppDataFolderName);
        return Path.Combine(baseDir, AppConstants.ConfigFileName);
    }

    /// <inheritdoc />
    public async Task ExportEncryptedProfileAsync(string password, string outputFilePath)
    {
        string configPath = GetConfigPath();

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            // 1. Add appsettings.json
            if (File.Exists(configPath)) archive.CreateEntryFromFile(configPath, AppConstants.ConfigFileName);
            
            // 2. Add decrypted rclone.conf (tokens are protected by the backup password, ensuring portability)
            string decryptedIni = await _configManager.GetDecryptedConfigAsync();
            if (!string.IsNullOrWhiteSpace(decryptedIni))
            {
                var rcloneEntry = archive.CreateEntry(AppConstants.RcloneConfigFileName);
                await using var entryStream = rcloneEntry.Open();
                await using var writer = new StreamWriter(entryStream, Encoding.UTF8);
                await writer.WriteAsync(decryptedIni);
            }
        }

        memoryStream.Position = 0;
        byte[] unencryptedZipBytes = memoryStream.ToArray();

        // Security parameters for AES-GCM
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[unencryptedZipBytes.Length];

        // Derive 256-bit key using PBKDF2
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, AppConstants.Pbkdf2Iterations, HashAlgorithmName.SHA256);
        byte[] key = pbkdf2.GetBytes(32);

        using (var aesGcm = new AesGcm(key, tag.Length))
        {
            aesGcm.Encrypt(nonce, unencryptedZipBytes, ciphertext, tag);
        }

        // File format: [FSBAK HEADER][SALT][NONCE][TAG][CIPHERTEXT]
        await using var fileStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write);
        await fileStream.WriteAsync(MagicHeader);
        await fileStream.WriteAsync(salt);
        await fileStream.WriteAsync(nonce);
        await fileStream.WriteAsync(tag);
        await fileStream.WriteAsync(ciphertext);
    }

    /// <inheritdoc />
    public async Task ImportEncryptedProfileAsync(string password, string inputFilePath)
    {
        string configPath = GetConfigPath();
        string rclonePath = _configManager.GetConfigPath();
        
        await using var fileStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read);

        if (fileStream.Length > MaxBackupSize)
            throw new InvalidOperationException("Backup file exceeds maximum allowed size (50MB).");

        byte[] header = new byte[MagicHeader.Length];
        if (await fileStream.ReadAsync(header) != MagicHeader.Length || !header.AsSpan().SequenceEqual(MagicHeader))
            throw new InvalidOperationException("Invalid backup file format.");

        byte[] salt = new byte[16];
        await fileStream.ReadExactlyAsync(salt);

        byte[] nonce = new byte[12];
        await fileStream.ReadExactlyAsync(nonce);

        byte[] tag = new byte[16];
        await fileStream.ReadExactlyAsync(tag);

        byte[] ciphertext = new byte[fileStream.Length - fileStream.Position];
        await fileStream.ReadExactlyAsync(ciphertext);

        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, AppConstants.Pbkdf2Iterations, HashAlgorithmName.SHA256);
        byte[] key = pbkdf2.GetBytes(32);

        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using var aesGcm = new AesGcm(key, tag.Length);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            throw new UnauthorizedAccessException("Incorrect password or backup file is corrupted.");
        }

        using var memoryStream = new MemoryStream(plaintext);
        using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);

        // 1. Extract appsettings.json
        var configEntry = archive.GetEntry(AppConstants.ConfigFileName);
        if (configEntry != null) configEntry.ExtractToFile(configPath, true);

        // 2. Extract and re-encrypt rclone.conf
        var rcloneEntry = archive.GetEntry(AppConstants.RcloneConfigFileName);
        if (rcloneEntry != null)
        {
            await using var entryStream = rcloneEntry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8);
            string iniContent = await reader.ReadToEndAsync();
            
            var lines = iniContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            
            // Re-encrypt any plaintext tokens found in the backup using the new machine's local vault
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("token = ", StringComparison.OrdinalIgnoreCase) && 
                    !trimmed.StartsWith("token = enc:", StringComparison.OrdinalIgnoreCase))
                {
                    string plainToken = trimmed.Substring(8).Trim();
                    string encryptedToken = _tokenCryptoService.EncryptToken(plainToken);
                    
                    int indentLength = lines[i].Length - trimmed.Length;
                    string indent = lines[i].Substring(0, indentLength);
                    
                    lines[i] = $"{indent}token = {encryptedToken}";
                }
            }
            
            await File.WriteAllLinesAsync(rclonePath, lines);
        }
    }
}