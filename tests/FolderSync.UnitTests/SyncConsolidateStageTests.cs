using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using FolderSync.Services.SyncStages;
using Moq;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for <see cref="SyncConsolidateStage"/>.
/// Validates merging orphaned folders into the target directory and handling various Drive states.
/// </summary>
public class SyncConsolidateStageTests
{
    private readonly Mock<IRcloneService> _mockRclone;
    private readonly Mock<IGoogleDriveApiService> _mockGoogleApi;
    private readonly Mock<ITranslationService> _mockLocalizer;
    private readonly SyncConsolidateStage _sut;

    public SyncConsolidateStageTests()
    {
        _mockRclone = new Mock<IRcloneService>();
        _mockGoogleApi = new Mock<IGoogleDriveApiService>();
        _mockLocalizer = new Mock<ITranslationService>();
        _mockLocalizer.Setup(l => l[It.IsAny<string>()]).Returns("Mocked String");

        _sut = new SyncConsolidateStage(_mockRclone.Object, _mockGoogleApi.Object, _mockLocalizer.Object);
    }

    private string GetMockJson(string createTime) =>
        $@"{{ ""chunkedPrompt"": {{ ""chunks"": [ {{ ""createTime"": ""{createTime}"" }} ] }} }}";

    [Fact]
    public async Task RunAsync_WhenNoOrphanFolders_ShouldPerformNoOperations()
    {
        // Arrange
        var remote = new RemoteInfo("Test", "gdrive_test", "target123");
        var dirs = new List<RcloneItem>
        {
            new RcloneItem("target123", AppConstants.TargetFolderName, DateTime.Now, true, "dir")
        }; 

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(dirs);

        // Act
        await _sut.RunAsync(remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        // No operations should be initiated if only the target directory exists.
        _mockRclone.Verify(x => x.ExecuteCommandAsync(It.IsAny<string[]>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Never);
        _mockGoogleApi.Verify(x => x.DeleteFolderIfOwnedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_WhenIdentitiesMatch_AndOrphanIsOlder_ShouldNotOverwriteTarget()
    {
        // Arrange
        var remote = new RemoteInfo("Test", "gdrive_test", "target123");
        var dirs = new List<RcloneItem>
        {
            new RcloneItem("target123", AppConstants.TargetFolderName, DateTime.Now, true, "dir"),
            new RcloneItem("orphan456", AppConstants.TargetFolderName, DateTime.Now, true, "dir")
        };

        var targetFile = new RcloneItem("f2", "Chat.prompt", DateTime.Now, false, AppConstants.AiStudioMimeType);
        var orphanFile = new RcloneItem("f1", "Chat.prompt", DateTime.Now.AddMinutes(-5), false, AppConstants.AiStudioMimeType); 

        _mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.EndsWith(":")), true, It.IsAny<CancellationToken>())).ReturnsAsync(dirs);
        _mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.Contains("orphan456")), false, It.IsAny<CancellationToken>())).ReturnsAsync(new List<RcloneItem> { orphanFile });
        _mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.Contains("target123")), false, It.IsAny<CancellationToken>())).ReturnsAsync(new List<RcloneItem> { targetFile });

        _mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), "orphan456", "Chat.prompt", It.IsAny<CancellationToken>())).ReturnsAsync(GetMockJson("2026-01-01T10:00:00Z"));
        _mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), "target123", "Chat.prompt", It.IsAny<CancellationToken>())).ReturnsAsync(GetMockJson("2026-01-01T10:00:00Z"));

        // Act
        await _sut.RunAsync(remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        // Older versions from the orphan should not overwrite matching or newer versions in the target directory.
        _mockRclone.Verify(x => x.ExecuteCommandAsync(It.Is<string[]>(a => a.Contains("moveto")), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_WhenReadingFileFails_ShouldNotThrow()
    {
        // Arrange
        var remote = new RemoteInfo("Test", "gdrive_test", "target123");
        var dirs = new List<RcloneItem>
        {
            new RcloneItem("target123", AppConstants.TargetFolderName, DateTime.Now, true, "dir"),
            new RcloneItem("orphan456", AppConstants.TargetFolderName, DateTime.Now, true, "dir")
        };
        var orphanFile = new RcloneItem("f1", "Chat.prompt", DateTime.Now, false, AppConstants.AiStudioMimeType);
        var targetFile = new RcloneItem("f2", "Chat.prompt", DateTime.Now, false, AppConstants.AiStudioMimeType);

        _mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.EndsWith(":")), true, It.IsAny<CancellationToken>())).ReturnsAsync(dirs);
        _mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.Contains("orphan456")), false, It.IsAny<CancellationToken>())).ReturnsAsync(new List<RcloneItem> { orphanFile });
        _mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.Contains("target123")), false, It.IsAny<CancellationToken>())).ReturnsAsync(new List<RcloneItem> { targetFile });

        // Simulate Rclone I/O error
        _mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("Rclone I/O error"));

        // Act & Assert
        // I/O failures should be intercepted and logged without terminating the entire synchronization pipeline.
        await _sut.Awaiting(x => x.RunAsync(remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None))
            .Should().NotThrowAsync();
    }
}
