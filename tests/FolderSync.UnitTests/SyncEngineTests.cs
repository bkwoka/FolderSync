using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using FolderSync.Services.SyncStages;
using Moq;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for <see cref="SyncSanitizeStage"/>.
/// Ensures that name collisions within a single Google Drive folder are resolved automatically.
/// </summary>
public class SyncSanitizeStageTests
{
    /// <summary>
    /// Verifies that when multiple files with the same name exist in a folder (possible in Google Drive but problematic for local FS),
    /// the sanitization stage renames the duplicates by adding a unique numerical suffix.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenDuplicateConversationsExist_ShouldRenameTheDuplicate()
    {
        // Arrange
        var mockRclone = new Mock<IRcloneService>();
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0);
        
        // Simulation: Two files with identical names (legal in Google Drive, but not on local OS)
        var originalFile = new RcloneItem("1", "MojaRozmowa", baseTime, false, AppConstants.AiStudioMimeType);
        var copyFile = new RcloneItem("2", "MojaRozmowa", baseTime.AddMinutes(5), false, AppConstants.AiStudioMimeType);
        
        mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<RcloneItem> { originalFile, copyFile });

        var mockLocalizer = new Mock<ITranslationService>();
        mockLocalizer.Setup(l => l[It.IsAny<string>()]).Returns((string s) => s);

        var sanitizeStage = new SyncSanitizeStage(mockRclone.Object, mockLocalizer.Object);
        var remote = new RemoteInfo("Master", "remote1", "folderId");
        
        // Act
        await sanitizeStage.RunAsync(remote, new Progress<FolderSync.Helpers.SyncProgressEvent>(), CancellationToken.None);

        // Assert
        // Expecting a 'moveto' command to be sent to rclone to resolve the name collision
        mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(args => args.Contains("moveto") && args.Any(a => a.Contains("MojaRozmowa"))), 
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<TimeSpan?>()), 
            Times.AtLeastOnce, 
            "The engine failed to initiate sanitization (renaming) for the duplicate conversation file.");
    }
}
