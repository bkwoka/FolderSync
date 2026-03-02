using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using FolderSync.Services;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for <see cref="ProfileCryptoService"/>.
/// Validates AES-GCM encryption correctness, password verification, file integrity checks,
/// and resilience against corrupted or tampered backup files.
/// 
/// IMPORTANT: These tests use real file system I/O in a temporary directory.
/// ConfigService reads from Environment.SpecialFolder.LocalApplicationData, so
/// ProfileCryptoService is tested in isolation using its public Export/Import contract.
/// </summary>
public class ProfileCryptoServiceTests : IDisposable
{
    private readonly ProfileCryptoService _sut;
    private readonly string _tempDir;
    private readonly string _backupFilePath;

    // A valid password meeting the MinPasswordLength=10 constraint.
    private const string ValidPassword = "SecurePass123!";

    public ProfileCryptoServiceTests()
    {
        _sut = new ProfileCryptoService();
        _tempDir = Path.Combine(Path.GetTempPath(), $"FolderSyncTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _backupFilePath = Path.Combine(_tempDir, "test_backup.fsbak");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort cleanup */ }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helper: Creates a valid .fsbak file using the service itself, then returns
    // the path so other tests can import from it.
    // ─────────────────────────────────────────────────────────────────────────────
    private async Task<string> CreateValidBackupAsync(string password = ValidPassword)
    {
        // Export always reads from %LocalAppData%/FolderSync. The files may not exist
        // on a CI machine, but the service handles that gracefully by creating an
        // empty ZIP archive. What matters is that the file format is valid.
        await _sut.ExportEncryptedProfileAsync(password, _backupFilePath);
        return _backupFilePath;
    }

    // ─── EXPORT ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_ShouldCreateNonEmptyFile()
    {
        // Act
        await _sut.ExportEncryptedProfileAsync(ValidPassword, _backupFilePath);

        // Assert
        File.Exists(_backupFilePath).Should().BeTrue("the backup file must be created on disk");
        new FileInfo(_backupFilePath).Length.Should().BeGreaterThan(0, "an encrypted file must not be empty");
    }

    [Fact]
    public async Task Export_ShouldWriteFSBAKMagicHeader()
    {
        // Act
        await _sut.ExportEncryptedProfileAsync(ValidPassword, _backupFilePath);

        // Assert
        // File format: [FSBAK (5 bytes)][SALT (16)][NONCE (12)][TAG (16)][CIPHERTEXT]
        byte[] header = new byte[5];
        await using var stream = File.OpenRead(_backupFilePath);
        await stream.ReadExactlyAsync(header);

        Encoding.ASCII.GetString(header).Should().Be("FSBAK",
            "the backup file must begin with the 'FSBAK' magic identifier for format validation");
    }

    [Fact]
    public async Task Export_CalledTwice_WithSamePassword_ShouldProduceDifferentBytes()
    {
        // Arrange – AES-GCM uses a random nonce and salt per call, so outputs must differ.
        string path2 = Path.Combine(_tempDir, "backup2.fsbak");

        // Act
        await _sut.ExportEncryptedProfileAsync(ValidPassword, _backupFilePath);
        await _sut.ExportEncryptedProfileAsync(ValidPassword, path2);

        // Assert
        byte[] bytes1 = await File.ReadAllBytesAsync(_backupFilePath);
        byte[] bytes2 = await File.ReadAllBytesAsync(path2);
        bytes1.Should().NotBeEquivalentTo(bytes2,
            "every export must use a fresh random salt and nonce (semantic security)");
    }

    // ─── IMPORT – HAPPY PATH ──────────────────────────────────────────────────────

    [Fact]
    public async Task Import_WithCorrectPassword_ShouldNotThrow()
    {
        // Arrange
        await CreateValidBackupAsync();

        // Act
        Func<Task> act = async () => await _sut.ImportEncryptedProfileAsync(ValidPassword, _backupFilePath);

        // Assert
        await act.Should().NotThrowAsync("a valid backup with the correct password must import cleanly");
    }

    // ─── IMPORT – WRONG PASSWORD ──────────────────────────────────────────────────

    [Fact]
    public async Task Import_WithWrongPassword_ShouldThrowUnauthorizedAccessException()
    {
        // Arrange
        await CreateValidBackupAsync(ValidPassword);

        // Act
        Func<Task> act = async () =>
            await _sut.ImportEncryptedProfileAsync("WrongPassword999!", _backupFilePath);

        // Assert
        // AES-GCM authentication tag verification will fail, and the service
        // wraps the CryptographicException into an UnauthorizedAccessException.
        await act.Should().ThrowExactlyAsync<UnauthorizedAccessException>(
            "an incorrect password must not silently succeed – it must be rejected by AES-GCM tag verification");
    }

    [Fact]
    public async Task Import_WithEmptyPassword_ShouldThrowUnauthorizedAccessException()
    {
        // Arrange – backup was created with a real password
        await CreateValidBackupAsync(ValidPassword);

        // Act
        Func<Task> act = async () =>
            await _sut.ImportEncryptedProfileAsync(string.Empty, _backupFilePath);

        // Assert
        await act.Should().ThrowExactlyAsync<UnauthorizedAccessException>(
            "an empty string produces a different PBKDF2 key, so decryption must fail");
    }

    // ─── IMPORT – CORRUPT / TAMPERED FILES ───────────────────────────────────────

    [Fact]
    public async Task Import_WithCorruptedCiphertext_ShouldThrowUnauthorizedAccessException()
    {
        // Arrange – export a valid backup, then flip a byte in the ciphertext region
        await CreateValidBackupAsync();
        byte[] bytes = await File.ReadAllBytesAsync(_backupFilePath);

        // Ciphertext starts after: FSBAK(5) + SALT(16) + NONCE(12) + TAG(16) = byte 49
        const int ciphertextOffset = 5 + 16 + 12 + 16;
        if (bytes.Length > ciphertextOffset)
            bytes[ciphertextOffset] ^= 0xFF; // Bit-flip to simulate tampering

        await File.WriteAllBytesAsync(_backupFilePath, bytes);

        // Act
        Func<Task> act = async () =>
            await _sut.ImportEncryptedProfileAsync(ValidPassword, _backupFilePath);

        // Assert
        // The AES-GCM authentication tag will not match the tampered ciphertext.
        await act.Should().ThrowExactlyAsync<UnauthorizedAccessException>(
            "any tampering with the ciphertext must be detected by the AEAD authentication tag");
    }

    [Fact]
    public async Task Import_WithTamperedAuthTag_ShouldThrowUnauthorizedAccessException()
    {
        // Arrange – flip a byte inside the 16-byte authentication tag
        await CreateValidBackupAsync();
        byte[] bytes = await File.ReadAllBytesAsync(_backupFilePath);

        // TAG starts at: FSBAK(5) + SALT(16) + NONCE(12) = byte 33
        const int tagOffset = 5 + 16 + 12;
        bytes[tagOffset] ^= 0xFF;
        await File.WriteAllBytesAsync(_backupFilePath, bytes);

        // Act
        Func<Task> act = async () =>
            await _sut.ImportEncryptedProfileAsync(ValidPassword, _backupFilePath);

        // Assert
        await act.Should().ThrowExactlyAsync<UnauthorizedAccessException>(
            "a modified authentication tag must be detected, preventing silent decryption of a tampered file");
    }

    [Fact]
    public async Task Import_WithInvalidMagicHeader_ShouldThrowInvalidOperationException()
    {
        // Arrange – write a file that starts with a wrong header (not "FSBAK")
        byte[] fakeFile = Encoding.ASCII.GetBytes("XXXXX" + new string('A', 100));
        await File.WriteAllBytesAsync(_backupFilePath, fakeFile);

        // Act
        Func<Task> act = async () =>
            await _sut.ImportEncryptedProfileAsync(ValidPassword, _backupFilePath);

        // Assert
        await act.Should().ThrowExactlyAsync<InvalidOperationException>(
            "files without the FSBAK magic header must be rejected before any decryption is attempted");
    }

    [Fact]
    public async Task Import_WithEmptyFile_ShouldThrow()
    {
        // Arrange – completely empty file
        await File.WriteAllBytesAsync(_backupFilePath, Array.Empty<byte>());

        // Act
        Func<Task> act = async () =>
            await _sut.ImportEncryptedProfileAsync(ValidPassword, _backupFilePath);

        // Assert
        await act.Should().ThrowAsync<Exception>(
            "an empty file is not a valid backup and must produce an exception");
    }

    [Fact]
    public async Task Import_WithFileLargerThan50MB_ShouldThrowInvalidOperationException()
    {
        // Arrange – create a file that exceeds the 50MB safety limit
        const long overLimit = 50L * 1024 * 1024 + 1;
        {
            using var fs = File.OpenWrite(_backupFilePath);
            fs.SetLength(overLimit);
            fs.WriteByte(0);
        }

        // Act
        Func<Task> act = async () =>
            await _sut.ImportEncryptedProfileAsync(ValidPassword, _backupFilePath);

        // Assert
        await act.Should().ThrowExactlyAsync<InvalidOperationException>(
            "files exceeding 50MB must be rejected to prevent decompression bomb attacks");
    }

    // ─── ROUND-TRIP ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportThenImport_WithSamePassword_ShouldCompleteFullRoundTrip()
    {
        // This is the most important integration-style test of the crypto service.
        // It verifies that the entire Export → Import pipeline is coherent.

        // Act
        await _sut.ExportEncryptedProfileAsync(ValidPassword, _backupFilePath);
        Func<Task> importAct = async () =>
            await _sut.ImportEncryptedProfileAsync(ValidPassword, _backupFilePath);

        // Assert
        await importAct.Should().NotThrowAsync(
            "a file encrypted with a password must always be decryptable with the same password");
    }
}
