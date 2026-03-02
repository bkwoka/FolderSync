using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FolderSync.Helpers;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using FolderSync.Services.SyncStages;
using Moq;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Tests for the HistoryFileName protection contract in <see cref="SyncConsolidateStage"/>.
///
/// Per-drive business rule: The file 'applet_access_history.json' stores metadata for the specific account.
/// Moving it during consolidation would corrupt the history of the target account.
/// </summary>
public class SyncConsolidateStageHistoryFileTests
{
    private readonly Mock<IRcloneService> _mockRclone;
    private readonly Mock<IGoogleDriveApiService> _mockGoogleApi;
    private readonly Mock<ITranslationService> _mockLocalizer;
    private readonly SyncConsolidateStage _sut;

    private const string TargetId = "target_folder_id";
    private const string OrphanId = "orphan_folder_id";
    private const string RemoteName = "gdrive_test";

    private readonly RemoteInfo _remote;

    public SyncConsolidateStageHistoryFileTests()
    {
        _mockRclone = new Mock<IRcloneService>();
        _mockGoogleApi = new Mock<IGoogleDriveApiService>();
        _mockLocalizer = new Mock<ITranslationService>();
        _mockLocalizer.Setup(l => l[It.IsAny<string>()]).Returns("{0}{1}");

        _remote = new RemoteInfo("Test Drive", RemoteName, TargetId);
        _sut = new SyncConsolidateStage(_mockRclone.Object, _mockGoogleApi.Object, _mockLocalizer.Object);
    }

    private void SetupTwoFolders()
    {
        var dirs = new List<RcloneItem>
        {
            new RcloneItem(TargetId, AppConstants.TargetFolderName, DateTime.UtcNow, true, "dir"),
            new RcloneItem(OrphanId, AppConstants.TargetFolderName, DateTime.UtcNow, true, "dir")
        };

        _mockRclone
            .Setup(x => x.ListItemsAsync(It.Is<string>(s => s == $"{RemoteName}:"), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirs);
    }

    private void SetupOrphanFiles(List<RcloneItem> files)
    {
        _mockRclone
            .Setup(x => x.ListItemsAsync(It.Is<string>(s => s.Contains(OrphanId)), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);
    }

    [Fact]
    public async Task RunAsync_WhenOrphanContainsHistoryFile_ShouldNeverMoveIt()
    {
        // Arrange
        SetupTwoFolders();
        var orphanFiles = new List<RcloneItem>
        {
            new RcloneItem("hist_id", AppConstants.HistoryFileName, DateTime.UtcNow, false, "application/json"),
            new RcloneItem("conv_id", "NormalChat.prompt", DateTime.UtcNow, false, AppConstants.AiStudioMimeType)
        };
        SetupOrphanFiles(orphanFiles);
        _mockRclone.Setup(x => x.ListItemsAsync(It.Is<string>(s => s.Contains(TargetId)), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RcloneItem>());

        // Act
        await _sut.RunAsync(_remote, new Progress<SyncProgressEvent>(), CancellationToken.None);

        // Assert
        _mockRclone.Verify(
            x => x.ExecuteCommandAsync(
                It.Is<string[]>(args => args[0] == "moveto" &&
                                        System.Array.Exists(args, a => a.Contains(AppConstants.HistoryFileName))),
                null, It.IsAny<CancellationToken>(), null),
            Times.Never);
    }

    [Theory]
    [InlineData("applet_access_history.json")]
    [InlineData("APPLET_ACCESS_HISTORY.JSON")]
    [InlineData("Applet_Access_History.Json")]
    public async Task RunAsync_HistoryFileExclusion_ShouldBeCaseInsensitive(string historyFileName)
    {
        // Arrange
        SetupTwoFolders();
        SetupOrphanFiles(new List<RcloneItem> { new RcloneItem("h1", historyFileName, DateTime.UtcNow, false, "application/json") });

        // Act
        await _sut.RunAsync(_remote, new Progress<SyncProgressEvent>(), CancellationToken.None);

        // Assert
        _mockRclone.Verify(
            x => x.ExecuteCommandAsync(
                It.Is<string[]>(args => args[0] == "moveto" &&
                                        System.Array.Exists(args, a => a.Contains(historyFileName))),
                null, It.IsAny<CancellationToken>(), null),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_AfterSuccessfulConsolidation_ShouldAttemptOrphanFolderDeletion()
    {
        // Arrange
        SetupTwoFolders();
        SetupOrphanFiles(new List<RcloneItem>());

        // Act
        await _sut.RunAsync(_remote, new Progress<SyncProgressEvent>(), CancellationToken.None);

        // Assert
        _mockGoogleApi.Verify(
            x => x.DeleteFolderIfOwnedAsync(RemoteName, OrphanId, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
