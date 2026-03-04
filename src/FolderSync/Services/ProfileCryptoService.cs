using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FolderSync.Services.Interfaces;
using FolderSync.Models;

namespace FolderSync.Services;

public class ProfileCryptoService : IProfileCryptoService
{
    private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("FSBAK");
    private const int MaxBackupSize = 50 * 1024 * 1024; 

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

    public async Task ExportEncryptedProfileAsync(string password, string outputFilePath)
    {
        string configPath = GetConfigPath();

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            if (File.Exists(configPath)) archive.CreateEntryFromFile(configPath, AppConstants.ConfigFileName);
            
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

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[unencryptedZipBytes.Length];

        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, AppConstants.Pbkdf2Iterations, HashAlgorithmName.SHA256);
        byte[] key = pbkdf2.GetBytes(32);

        using (var aesGcm = new AesGcm(key, tag.Length))
        {
            aesGcm.Encrypt(nonce, unencryptedZipBytes, ciphertext, tag);
        }

        await using var fileStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write);
        await fileStream.WriteAsync(MagicHeader);
        await fileStream.WriteAsync(salt);
        await fileStream.WriteAsync(nonce);
        await fileStream.WriteAsync(tag);
        await fileStream.WriteAsync(ciphertext);
    }

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
            
            for (int i = 0; i < lines.Length; i++)
            {
                if (TryParseTokenLine(lines[i], out string prefix, out string tokenValue) && !tokenValue.StartsWith("enc:", StringComparison.OrdinalIgnoreCase))
                {
                    string encryptedToken = _tokenCryptoService.EncryptToken(tokenValue);
                    lines[i] = $"{prefix} {encryptedToken}";
                }
            }
            
            await File.WriteAllLinesAsync(rclonePath, lines);
        }
    }
}