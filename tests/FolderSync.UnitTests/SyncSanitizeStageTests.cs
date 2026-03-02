using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using FolderSync.Services.SyncStages;
using Moq;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for <see cref="SyncSanitizeStage"/>.
/// Validates filename collision resolution and file type filtering.
/// </summary>
public class SyncSanitizeStageTests
{
    private readonly Mock<IRcloneService> _mockRclone;
    private readonly Mock<ITranslationService> _mockLocalizer;
    private readonly SyncSanitizeStage _sut;
    private readonly RemoteInfo _remote;

    public SyncSanitizeStageTests()
    {
        _mockRclone = new Mock<IRcloneService>();
        _mockLocalizer = new Mock<ITranslationService>();
        _mockLocalizer.Setup(l => l[It.IsAny<string>()]).Returns("MockedString");
        _remote = new RemoteInfo("Test", "remote1", "folder1");
        _sut = new SyncSanitizeStage(_mockRclone.Object, _mockLocalizer.Object);
    }

    [Fact]
    public async Task RunAsync_WithTripleDuplicates_ShouldRenameOnlyTwoNewest()
    {
        // Arrange
        var baseTime = new DateTime(2026, 1, 1, 10, 0, 0);
        var duplicateFiles = new List<RcloneItem>
        {
            new RcloneItem("id1", "MyChat.prompt", baseTime, false, AppConstants.AiStudioMimeType), // Oldest (Preserve original name)
            new RcloneItem("id2", "MyChat.prompt", baseTime.AddMinutes(5), false, AppConstants.AiStudioMimeType), // Rename required
            new RcloneItem("id3", "MyChat.prompt", baseTime.AddMinutes(10), false, AppConstants.AiStudioMimeType) // Rename required
        };

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(duplicateFiles);

        // Act
        var progress = new Progress<FolderSync.Helpers.SyncProgressEvent>();
        await _sut.RunAsync(_remote, progress, CancellationToken.None);

        // Assert
        // Verify that 'moveto' was executed exactly twice for the two newest duplicates.
        _mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(args => args[0] == "moveto"), 
            null, It.IsAny<CancellationToken>(), null), 
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunAsync_WithExactlyTwoDuplicates_ShouldRenameOnlyOneNewest()
    {
        // Arrange
        var baseTime = new DateTime(2026, 1, 1, 10, 0, 0);
        var duplicateFiles = new List<RcloneItem>
        {
            new RcloneItem("id1", "MyChat.prompt", baseTime, false, AppConstants.AiStudioMimeType), // Keep
            new RcloneItem("id2", "MyChat.prompt", baseTime.AddMinutes(5), false, AppConstants.AiStudioMimeType) // Rename
        };

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(duplicateFiles);

        // Act
        await _sut.RunAsync(_remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        // Only the single newest duplicate should be rebranded.
        _mockRclone.Verify(x => x.ExecuteCommandAsync(It.Is<string[]>(args => args[0] == "moveto"), null, It.IsAny<CancellationToken>(), null), Times.Exactly(1));
    }

    [Fact]
    public async Task RunAsync_WithEmptyDrive_ShouldPerformNoOperations()
    {
        // Arrange
        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RcloneItem>()); 

        // Act
        await _sut.RunAsync(_remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        // No operations should be performed on an empty drive.
        _mockRclone.Verify(x => x.ExecuteCommandAsync(It.IsAny<string[]>(), null, It.IsAny<CancellationToken>(), null), Times.Never);
    }

    [Fact]
    public async Task RunAsync_WithNonAiStudioFiles_ShouldIgnoreThem()
    {
        // Arrange
        var files = new List<RcloneItem>
        {
            new RcloneItem("id1", "DuplicateName.png", DateTime.Now, false, "image/png"),
            new RcloneItem("id2", "DuplicateName.png", DateTime.Now.AddMinutes(5), false, "image/png")
        };

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        await _sut.RunAsync(_remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        // Sanitization stage should only target application-specific .prompt files.
        _mockRclone.Verify(x => x.ExecuteCommandAsync(It.IsAny<string[]>(), null, It.IsAny<CancellationToken>(), null), Times.Never);
    }
}
