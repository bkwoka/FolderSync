using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FolderSync.Services.Interfaces;

namespace FolderSync.Services;

/// <summary>
/// Service providing secure backup and restore functionality for application profiles.
/// Uses AES-GCM for authenticated encryption and PBKDF2 for key derivation.
/// </summary>
public class ProfileCryptoService : IProfileCryptoService
{
    private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("FSBAK");
    private const int MaxBackupSize = 50 * 1024 * 1024; // 50MB safety limit

    private (string configPath, string rclonePath) GetPaths()
    {
        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppConstants.AppDataFolderName);
        return (Path.Combine(baseDir, AppConstants.ConfigFileName),
            Path.Combine(baseDir, AppConstants.RcloneConfigFileName));
    }

    /// <inheritdoc />
    public async Task ExportEncryptedProfileAsync(string password, string outputFilePath)
    {
        var (configPath, rclonePath) = GetPaths();

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            if (File.Exists(configPath)) archive.CreateEntryFromFile(configPath, AppConstants.ConfigFileName);
            if (File.Exists(rclonePath)) archive.CreateEntryFromFile(rclonePath, AppConstants.RcloneConfigFileName);
        }

        memoryStream.Position = 0;
        byte[] unencryptedZipBytes = memoryStream.ToArray();

        // Security parameters for AES-GCM
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(12); // Standard GCM nonce size
        byte[] tag = new byte[16]; // Integrity tag
        byte[] ciphertext = new byte[unencryptedZipBytes.Length];

        // Derive 256-bit key using PBKDF2
        using var pbkdf2 =
            new Rfc2898DeriveBytes(password, salt, AppConstants.Pbkdf2Iterations, HashAlgorithmName.SHA256);
        byte[] key = pbkdf2.GetBytes(32);

        using (var aesGcm = new AesGcm(key, tag.Length))
        {
            aesGcm.Encrypt(nonce, unencryptedZipBytes, ciphertext, tag);
        }

        // File format: [FSBAK HEADER][SALT][NONCE][TAG][CIPHERTEXT]
        using var fileStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write);
        await fileStream.WriteAsync(MagicHeader);
        await fileStream.WriteAsync(salt);
        await fileStream.WriteAsync(nonce);
        await fileStream.WriteAsync(tag);
        await fileStream.WriteAsync(ciphertext);
    }

    /// <inheritdoc />
    public async Task ImportEncryptedProfileAsync(string password, string inputFilePath)
    {
        var (configPath, rclonePath) = GetPaths();
        using var fileStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read);

        // Security check for malicious or incorrect file size
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

        using var pbkdf2 =
            new Rfc2898DeriveBytes(password, salt, AppConstants.Pbkdf2Iterations, HashAlgorithmName.SHA256);
        byte[] key = pbkdf2.GetBytes(32);

        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using var aesGcm = new AesGcm(key, tag.Length);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            // Decryption failure indicates wrong password or file tampering
            throw new UnauthorizedAccessException("Incorrect password or backup file is corrupted.");
        }

        using var memoryStream = new MemoryStream(plaintext);
        using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);

        var configEntry = archive.GetEntry(AppConstants.ConfigFileName);
        if (configEntry != null) configEntry.ExtractToFile(configPath, true);

        var rcloneEntry = archive.GetEntry(AppConstants.RcloneConfigFileName);
        if (rcloneEntry != null) rcloneEntry.ExtractToFile(rclonePath, true);
    }
}