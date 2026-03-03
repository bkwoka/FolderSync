using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using FolderSync.Services.SyncStages;
using Moq;
using Xunit;
using FluentAssertions;

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

    // ─── Unique Files ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithSingleUniqueFile_ShouldPerformNoOperations()
    {
        // Arrange
        var files = new List<RcloneItem>
        {
            new RcloneItem("id1", "UniqueChat.prompt", DateTime.Now, false, AppConstants.AiStudioMimeType)
        };

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        await _sut.RunAsync(_remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        _mockRclone.Verify(x => x.ExecuteCommandAsync(It.IsAny<string[]>(), null, It.IsAny<CancellationToken>(), null), 
            Times.Never, "a single unique file does not require sanitization");
    }

    // ─── Duplicates Logic ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithTripleDuplicates_ShouldRenameOnlyTwoNewest()
    {
        // Arrange
        var baseTime = new DateTime(2026, 1, 1, 10, 0, 0);
        var duplicateFiles = new List<RcloneItem>
        {
            new RcloneItem("id1", "MyChat.prompt", baseTime, false, AppConstants.AiStudioMimeType),
            new RcloneItem("id2", "MyChat.prompt", baseTime.AddMinutes(5), false, AppConstants.AiStudioMimeType),
            new RcloneItem("id3", "MyChat.prompt", baseTime.AddMinutes(10), false, AppConstants.AiStudioMimeType)
        };

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(duplicateFiles);

        // Act
        await _sut.RunAsync(_remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        _mockRclone.Verify(x => x.ExecuteCommandAsync(It.Is<string[]>(args => args[0] == "moveto"), 
            null, It.IsAny<CancellationToken>(), null), Times.Exactly(2));
    }

    [Fact]
    public async Task RunAsync_WithDuplicatesHavingIdenticalModTime_ShouldRenameExactlyOne()
    {
        // Arrange
        var sameTime = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var files = new List<RcloneItem>
        {
            new RcloneItem("id1", "Chat.prompt", sameTime, false, AppConstants.AiStudioMimeType),
            new RcloneItem("id2", "Chat.prompt", sameTime, false, AppConstants.AiStudioMimeType),
        };

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        await _sut.RunAsync(_remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        _mockRclone.Verify(x => x.ExecuteCommandAsync(It.Is<string[]>(a => a[0] == "moveto"), 
            null, It.IsAny<CancellationToken>(), null), Times.Exactly(1),
            "with identical ModTime, exactly one duplicate must be renamed");
    }

    [Fact]
    public async Task RunAsync_WithMultipleDuplicateGroups_ShouldRenameInEachGroupIndependently()
    {
        // Arrange
        var baseTime = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var files = new List<RcloneItem>
        {
            new RcloneItem("a1", "Alpha.prompt", baseTime, false, AppConstants.AiStudioMimeType),
            new RcloneItem("a2", "Alpha.prompt", baseTime.AddMinutes(5), false, AppConstants.AiStudioMimeType),
            new RcloneItem("b1", "Beta.prompt", baseTime, false, AppConstants.AiStudioMimeType),
            new RcloneItem("b2", "Beta.prompt", baseTime.AddMinutes(3), false, AppConstants.AiStudioMimeType),
            new RcloneItem("b3", "Beta.prompt", baseTime.AddMinutes(7), false, AppConstants.AiStudioMimeType),
        };

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        await _sut.RunAsync(_remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        _mockRclone.Verify(x => x.ExecuteCommandAsync(It.Is<string[]>(a => a[0] == "moveto"), 
            null, It.IsAny<CancellationToken>(), null), Times.Exactly(3),
            "each duplicate group must be processed independently");
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

    // ─── Filtering ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithMixedMimeTypes_ShouldOnlySanitizeAiStudioFiles()
    {
        // Arrange
        var baseTime = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var files = new List<RcloneItem>
        {
            new RcloneItem("p1", "Chat.prompt", baseTime, false, AppConstants.AiStudioMimeType),
            new RcloneItem("p2", "Chat.prompt", baseTime.AddMinutes(5), false, AppConstants.AiStudioMimeType),
            new RcloneItem("d1", "Report.docx", baseTime, false, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            new RcloneItem("d2", "Report.docx", baseTime.AddMinutes(5), false, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        };

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        await _sut.RunAsync(_remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        _mockRclone.Verify(x => x.ExecuteCommandAsync(It.Is<string[]>(a => a[0] == "moveto"), 
            null, It.IsAny<CancellationToken>(), null), Times.Exactly(1),
            "only files matching the AI Studio MIME type should be processed");
    }

    // ─── Resilience & Cancellation ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenMovetoThrowsForOneDuplicate_ShouldFailFastToPreventCorruption()
    {
        // Arrange
        var baseTime = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var files = new List<RcloneItem>
        {
            new RcloneItem("id1", "Chat.prompt", baseTime, false, AppConstants.AiStudioMimeType),
            new RcloneItem("id2", "Chat.prompt", baseTime.AddMinutes(5), false, AppConstants.AiStudioMimeType),
        };

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _mockRclone.Setup(x => x.ExecuteCommandAsync(It.Is<string[]>(a => a[0] == "moveto"), 
            null, It.IsAny<CancellationToken>(), null))
            .ThrowsAsync(new InvalidOperationException("Rclone: file locked by another process"));

        // Act & Assert
        await _sut.Awaiting(x => x.RunAsync(_remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>("sanitization failures must halt the process to maintain data integrity");
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_ShouldPropagateOperationCanceledException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act & Assert
        await _sut.Awaiting(x => x.RunAsync(_remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
