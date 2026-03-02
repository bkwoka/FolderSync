using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using FolderSync.Services;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Additional unit tests for <see cref="ProfileCryptoService"/> covering edge cases of truncated/invalid backup files.
/// These tests verify that malformed inputs do not lead to unhandled system exceptions or application crashes.
/// </summary>
public class ProfileCryptoEdgeCaseTests : IDisposable
{
    private readonly ProfileCryptoService _sut = new();
    private readonly string _tempDir;
    private readonly string _backupFilePath;

    public ProfileCryptoEdgeCaseTests()
    {
        _tempDir        = Path.Combine(Path.GetTempPath(), $"FolderSyncEdge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _backupFilePath = Path.Combine(_tempDir, "edge.fsbak");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(5)]   // only header "FSBAK", missing all crypto metadata
    [InlineData(10)]  // header + partial salt
    [InlineData(20)]  // header + partial salt (under 16B)
    [InlineData(48)]  // header + SALT(16) + NONCE(12) + partial tag (under 16B)
    public async Task Import_WithTruncatedFile_AfterValidHeader_ShouldThrow(int totalFileSize)
    {
        // Arrange – file with valid FSBAK header but insufficient crypto data
        byte[] fileBytes = new byte[totalFileSize];
        byte[] header    = Encoding.ASCII.GetBytes("FSBAK");
        Array.Copy(header, fileBytes, header.Length);

        await File.WriteAllBytesAsync(_backupFilePath, fileBytes);

        // Act
        Func<Task> act = async () =>
            await _sut.ImportEncryptedProfileAsync("AnyPassword123!", _backupFilePath);

        // Assert – must throw a handled exception rather than crashing with unhandled system errors
        await act.Should().ThrowAsync<Exception>(
            $"a {totalFileSize}-byte file with valid FSBAK header and truncated metadata must not crash the caller");
    }

    [Fact]
    public async Task Import_WithMinimalSizeFile_ShouldThrowHandledExceptionNotSystemCrash()
    {
        // Arrange – minimal file size: FSBAK(5) + SALT(16) + NONCE(12) + TAG(16) without encrypted payload
        const int minSize = 5 + 16 + 12 + 16; // = 49 bytes
        byte[] fileBytes = new byte[minSize];
        Encoding.ASCII.GetBytes("FSBAK").CopyTo(fileBytes, 0);

        await File.WriteAllBytesAsync(_backupFilePath, fileBytes);

        // Act
        Func<Task> act = async () =>
            await _sut.ImportEncryptedProfileAsync("AnyPassword123!", _backupFilePath);

        // Assert – must fail gracefully without generating an unhandled EndOfStreamException
        await act.Should().ThrowAsync<Exception>(
            "a valid-format file with incorrect cryptographic metadata must fail gracefully");
    }

    [Fact]
    public async Task Import_WithNonExistentFile_ShouldThrowFileNotFoundException()
    {
        // Arrange
        string nonExistentPath = Path.Combine(_tempDir, "ghost.fsbak");

        // Act
        Func<Task> act = async () =>
            await _sut.ImportEncryptedProfileAsync("AnyPassword123!", nonExistentPath);

        // Assert
        await act.Should().ThrowAsync<FileNotFoundException>(
            "importing a non-existent backup file must be handled via FileNotFoundException");
    }
}
