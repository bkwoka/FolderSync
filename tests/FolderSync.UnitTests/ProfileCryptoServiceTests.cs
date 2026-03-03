using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using Moq;
using Xunit;

namespace FolderSync.UnitTests;

public class ProfileCryptoServiceTests : IDisposable
{
    private readonly ProfileCryptoService _sut;
    private readonly Mock<IRcloneConfigManager> _mockConfigManager;
    private readonly Mock<ITokenCryptoService> _mockTokenCrypto;
    private readonly string _tempDir;
    private readonly string _backupFilePath;

    private const string ValidPassword = "SecurePass123!";

    public ProfileCryptoServiceTests()
    {
        _mockConfigManager = new Mock<IRcloneConfigManager>();
        _mockTokenCrypto = new Mock<ITokenCryptoService>();
        
        // Mock the config manager to return a dummy INI string during export
        _mockConfigManager.Setup(x => x.GetDecryptedConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("[remote]\ntoken = {\"access_token\":\"dummy\"}");
            
        // Mock the crypto service to simulate encryption during import
        _mockTokenCrypto.Setup(x => x.EncryptToken(It.IsAny<string>()))
            .Returns("enc:dummy_encrypted_token");

        _sut = new ProfileCryptoService(_mockConfigManager.Object, _mockTokenCrypto.Object);
        
        _tempDir = Path.Combine(Path.GetTempPath(), $"FolderSyncTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _backupFilePath = Path.Combine(_tempDir, "test_backup.fsbak");
        
        // Setup dummy paths for the service to write to during import
        _mockConfigManager.Setup(x => x.GetConfigPath()).Returns(Path.Combine(_tempDir, "rclone.conf"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort cleanup */ }
    }

    private async Task<string> CreateValidBackupAsync(string password = ValidPassword)
    {
        await _sut.ExportEncryptedProfileAsync(password, _backupFilePath);
        return _backupFilePath;
    }

    [Fact]
    public async Task Export_ShouldCreateNonEmptyFile()
    {
        await _sut.ExportEncryptedProfileAsync(ValidPassword, _backupFilePath);
        File.Exists(_backupFilePath).Should().BeTrue();
        new FileInfo(_backupFilePath).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Export_ShouldWriteFSBAKMagicHeader()
    {
        await _sut.ExportEncryptedProfileAsync(ValidPassword, _backupFilePath);
        byte[] header = new byte[5];
        await using var stream = File.OpenRead(_backupFilePath);
        await stream.ReadExactlyAsync(header);
        Encoding.ASCII.GetString(header).Should().Be("FSBAK");
    }

    [Fact]
    public async Task Import_WithCorrectPassword_ShouldNotThrow()
    {
        await CreateValidBackupAsync();
        Func<Task> act = async () => await _sut.ImportEncryptedProfileAsync(ValidPassword, _backupFilePath);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Import_WithWrongPassword_ShouldThrowUnauthorizedAccessException()
    {
        await CreateValidBackupAsync(ValidPassword);
        Func<Task> act = async () => await _sut.ImportEncryptedProfileAsync("WrongPassword999!", _backupFilePath);
        await act.Should().ThrowExactlyAsync<UnauthorizedAccessException>();
    }
    
    [Fact]
    public async Task Import_ShouldEncryptPlaintextTokensFromBackup()
    {
        // Arrange
        await CreateValidBackupAsync();
        
        // Act
        await _sut.ImportEncryptedProfileAsync(ValidPassword, _backupFilePath);
        
        // Assert
        _mockTokenCrypto.Verify(x => x.EncryptToken("{\"access_token\":\"dummy\"}"), Times.Once, 
            "Import process must encrypt plaintext tokens found in the backup before saving to disk.");
    }
}
