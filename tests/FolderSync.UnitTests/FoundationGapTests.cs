using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using Moq;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for <see cref="ConfigService"/> and <see cref="DeleteOrchestratorService"/> 
/// covering resilience edge cases and functional gaps identified during code audit.
/// </summary>
public class FoundationGapTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public FoundationGapTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FolderSyncResilienceTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, AppConstants.ConfigFileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup */ }
    }

    private TestableConfigService CreateConfigService() => new(_tempDir);

    // ─── ConfigService Forward Compatibility ─────────────────────────────────────

    [Fact]
    public async Task LoadConfig_WhenFileContainsUnknownJsonFields_ShouldIgnoreThem()
    {
        // Arrange
        const string json = """
            {
              "masterRemoteId": "future_id",
              "newFeatureFlag": true,
              "remotes": []
            }
            """;
        await File.WriteAllTextAsync(_configFilePath, json, Encoding.UTF8);
        var sut = CreateConfigService();

        // Act
        var config = await sut.LoadConfigAsync();

        // Assert
        config.MasterRemoteId.Should().Be("future_id");
    }

    [Fact]
    public async Task LoadConfig_WhenParentDirectoryIsDeleted_ShouldReturnEmptyConfig()
    {
        // Arrange
        var sut = CreateConfigService();
        Directory.Delete(_tempDir, recursive: true);

        // Act
        var config = await sut.LoadConfigAsync();

        // Assert
        config.Should().NotBeNull();
        config.Remotes.Should().BeEmpty();
    }

    // ─── DeleteOrchestratorService: deleteAttachments: false Contract ────────────

    [Fact]
    public async Task DeleteConversation_WhenDeleteAttachmentsFalse_ShouldSkipAttachmentPhase()
    {
        // Arrange
        var mockRclone = new Mock<IRcloneService>();
        var mockGoogleApi = new Mock<IGoogleDriveApiService>();
        var sut = new DeleteOrchestratorService(mockRclone.Object, mockGoogleApi.Object);

        var r = new RemoteInfo("M", "rm", "f1");
        var list = new List<RemoteInfo> { r };

        mockRclone
            .Setup(x => x.ListItemsAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RcloneItem>());

        // Act
        await sut.DeleteConversationAsync("test.prompt", false, r, list, CancellationToken.None);

        // Assert
        mockRclone.Verify(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mockGoogleApi.Verify(x => x.TrashFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
