using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using Moq;
using Xunit;

namespace FolderSync.UnitTests;

public class ProfileCryptoEdgeCaseTests : IDisposable
{
    private readonly ProfileCryptoService _sut;
    private readonly string _tempDir;
    private readonly string _backupFilePath;

    public ProfileCryptoEdgeCaseTests()
    {
        var mockConfigManager = new Mock<IRcloneConfigManager>();
        var mockTokenCrypto = new Mock<ITokenCryptoService>();
        
        _sut = new ProfileCryptoService(mockConfigManager.Object, mockTokenCrypto.Object);
        
        _tempDir        = Path.Combine(Path.GetTempPath(), $"FolderSyncEdge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _backupFilePath = Path.Combine(_tempDir, "edge.fsbak");
        
        mockConfigManager.Setup(x => x.GetConfigPath()).Returns(Path.Combine(_tempDir, "rclone.conf"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(5)]   
    [InlineData(10)]  
    [InlineData(20)]
    [InlineData(48)]  
    public async Task Import_WithTruncatedFile_AfterValidHeader_ShouldThrow(int totalFileSize)
    {
        byte[] fileBytes = new byte[totalFileSize];
        byte[] header    = Encoding.ASCII.GetBytes("FSBAK");
        Array.Copy(header, fileBytes, header.Length);

        await File.WriteAllBytesAsync(_backupFilePath, fileBytes);

        Func<Task> act = async () => await _sut.ImportEncryptedProfileAsync("AnyPassword123!", _backupFilePath);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Import_WithMinimalSizeFile_ShouldThrowHandledExceptionNotSystemCrash()
    {
        const int minSize = 5 + 16 + 12 + 16; 
        byte[] fileBytes = new byte[minSize];
        Encoding.ASCII.GetBytes("FSBAK").CopyTo(fileBytes, 0);

        await File.WriteAllBytesAsync(_backupFilePath, fileBytes);

        Func<Task> act = async () => await _sut.ImportEncryptedProfileAsync("AnyPassword123!", _backupFilePath);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Import_WithNonExistentFile_ShouldThrowFileNotFoundException()
    {
        string nonExistentPath = Path.Combine(_tempDir, "ghost.fsbak");
        Func<Task> act = async () => await _sut.ImportEncryptedProfileAsync("AnyPassword123!", nonExistentPath);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }
}
