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
/// Validates complex logic for merging orphaned folders and handling file name collisions inside a single drive.
/// </summary>
public class SyncConsolidateStageTests
{
    /// <summary>
    /// Helper to generate a simulated JSON structure for a Google AI Studio prompt file.
    /// </summary>
    private string GetMockJson(string createTime) =>
        $@"{{ ""chunkedPrompt"": {{ ""chunks"": [ {{ ""createTime"": ""{createTime}"" }} ] }} }}";

    /// <summary>
    /// Verifies that when two files have the same name but different internal AI Studio identities (createTime),
    /// the orphaned file is renamed with a hash suffix to prevent data loss.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenIdentitiesMismatch_ShouldRenameOrphanFile()
    {
        // Arrange
        var mockRclone = new Mock<IRcloneService>();
        
        var targetId = "target123";
        var orphanId = "orphan456";
        var remote = new RemoteInfo("Test", "gdrive_test", targetId);

        // Simulation: Found one primary folder and one orphan folder
        var dirs = new List<RcloneItem>
        {
            new(targetId, AppConstants.TargetFolderName, DateTime.Now, true, "dir"),
            new(orphanId, AppConstants.TargetFolderName, DateTime.Now, true, "dir")
        };

        // Simulation: Identical file names in both folders
        var orphanFile = new RcloneItem("f1", "Chat.prompt", DateTime.Now, false, AppConstants.AiStudioMimeType);
        var targetFile = new RcloneItem("f2", "Chat.prompt", DateTime.Now.AddMinutes(-5), false, AppConstants.AiStudioMimeType);

        mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.EndsWith(":")), true, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(dirs);
                  
        mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.Contains(orphanId)), false, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<RcloneItem> { orphanFile });
                  
        mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.Contains(targetId)), false, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<RcloneItem> { targetFile });

        // Identities mismatch: Different createTime in JSON (distinct conversations)
        mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), orphanId, "Chat.prompt", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(GetMockJson("2026-01-01T10:00:00Z"));
        mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), targetId, "Chat.prompt", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(GetMockJson("2026-01-01T12:00:00Z"));

        var mockLocalizer = new Mock<ITranslationService>();
        mockLocalizer.Setup(l => l[It.IsAny<string>()]).Returns((string s) => s);

        var stage = new SyncConsolidateStage(mockRclone.Object, mockLocalizer.Object);

        // Act
        await stage.RunAsync(remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        // Since these are different conversations, neither should be deleted
        mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(a => a.Contains("deletefile")), null, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Never);
            
        // The orphaned file must be moved to primary folder with a sanitized, suffixed name
        mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(a => a.Contains("moveto") && a[2].Contains("_") && a[2].EndsWith(".prompt")), 
            null, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Once);
    }

    /// <summary>
    /// Verifies that when two files share the same identity (createTime), but the orphaned version is newer,
    /// the target version is overwritten to reflect the latest changes.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenIdentitiesMatch_AndOrphanIsNewer_ShouldOverwriteTarget()
    {
        // Arrange
        var mockRclone = new Mock<IRcloneService>();
        var targetId = "target123";
        var orphanId = "orphan456";
        var remote = new RemoteInfo("Test", "gdrive_test", targetId);

        var dirs = new List<RcloneItem>
        {
            new(targetId, AppConstants.TargetFolderName, DateTime.Now, true, "dir"),
            new(orphanId, AppConstants.TargetFolderName, DateTime.Now, true, "dir")
        };

        // Orphan is newer (+5 minutes)
        var orphanFile = new RcloneItem("f1", "Chat.prompt", DateTime.Now.AddMinutes(5), false, AppConstants.AiStudioMimeType);
        var targetFile = new RcloneItem("f2", "Chat.prompt", DateTime.Now, false, AppConstants.AiStudioMimeType);

        mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.EndsWith(":")), true, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(dirs);
        mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.Contains(orphanId)), false, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<RcloneItem> { orphanFile });
        mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.Contains(targetId)), false, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<RcloneItem> { targetFile });

        // Identity match: Identical createTime in JSON (same conversation session)
        mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), orphanId, "Chat.prompt", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(GetMockJson("2026-01-01T10:00:00Z"));
        mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), targetId, "Chat.prompt", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(GetMockJson("2026-01-01T10:00:00Z"));

        var mockLocalizer = new Mock<ITranslationService>();
        mockLocalizer.Setup(l => l[It.IsAny<string>()]).Returns((string s) => s);

        var stage = new SyncConsolidateStage(mockRclone.Object, mockLocalizer.Object);

        // Act
        await stage.RunAsync(remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        // Target is old, delete it to make space for the updated orphan
        mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(a => a.Contains("deletefile")), null, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Once);
            
        // Orphan is moved to primary with its original name
        mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(a => a.Contains("moveto") && a[2].EndsWith("Chat.prompt")), 
            null, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Once);
    }
}
