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
/// Extended unit tests for <see cref="SyncConsolidateStage"/> covering the critical
/// business paths that were missing from the original test suite:
///
///  1. Orphan has a NEWER version of a same-identity file → must overwrite target
///  2. Orphan has a file NOT present in target at all → must move without conflict logic
///  3. Same file name but DIFFERENT identity (createTime mismatch) → must rename orphan file
///  4. CancellationToken is respected during per-file processing loop
///  5. Drive with no target folder at all (empty listing) → no operations
///  6. history file is never moved regardless of conditions
/// </summary>
public class SyncConsolidateStageExtendedTests
{
    private readonly Mock<IRcloneService>       _mockRclone;
    private readonly Mock<IGoogleDriveApiService> _mockGoogleApi;
    private readonly Mock<ITranslationService>  _mockLocalizer;
    private readonly SyncConsolidateStage       _sut;

    // Fixed IDs used across tests for clarity
    private const string TargetId = "target_folder_id";
    private const string OrphanId = "orphan_folder_id";
    private const string RemoteName = "gdrive_test";

    private readonly RemoteInfo _remote;

    public SyncConsolidateStageExtendedTests()
    {
        _mockRclone    = new Mock<IRcloneService>();
        _mockGoogleApi = new Mock<IGoogleDriveApiService>();
        _mockLocalizer = new Mock<ITranslationService>();
        _mockLocalizer.Setup(l => l[It.IsAny<string>()]).Returns("Mocked");
        _mockLocalizer.Setup(l => l[It.IsAny<string>()]).Returns("{0}{1}");

        _remote = new RemoteInfo("Test Drive", RemoteName, TargetId);
        _sut = new SyncConsolidateStage(_mockRclone.Object, _mockGoogleApi.Object, _mockLocalizer.Object);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    private static string MakeJson(string createTime) =>
        $@"{{ ""chunkedPrompt"": {{ ""chunks"": [ {{ ""createTime"": ""{createTime}"" }} ] }} }}";

    private static List<RcloneItem> TwoDirsListing() => new()
    {
        new RcloneItem(TargetId, AppConstants.TargetFolderName, DateTime.Now, true, "dir"),
        new RcloneItem(OrphanId, AppConstants.TargetFolderName, DateTime.Now, true, "dir")
    };

    private void SetupDirListing(List<RcloneItem> dirs)
    {
        _mockRclone
            .Setup(x => x.ListItemsAsync(It.Is<string>(s => s.EndsWith(":")), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirs);
    }

    private void SetupFileListing(string folderId, List<RcloneItem> files)
    {
        _mockRclone
            .Setup(x => x.ListItemsAsync(It.Is<string>(s => s.Contains(folderId)), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);
    }

    private void SetupFileContent(string folderId, string fileName, string json)
    {
        _mockRclone
            .Setup(x => x.ReadFileContentAsync(RemoteName, folderId, fileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);
    }

    // ─── Test 1: Orphan is NEWER → must overwrite ─────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenIdentitiesMatch_AndOrphanIsNewer_ShouldDeleteTargetAndMoveOrphan()
    {
        // Arrange
        const string fileName = "Chat.prompt";
        var baseTime = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        // Orphan file is 10 minutes NEWER than the target file (and the 2-second threshold is met)
        var orphanFile = new RcloneItem("f_orphan", fileName, baseTime.AddMinutes(10), false, AppConstants.AiStudioMimeType);
        var targetFile = new RcloneItem("f_target", fileName, baseTime, false, AppConstants.AiStudioMimeType);

        SetupDirListing(TwoDirsListing());
        SetupFileListing(OrphanId, new List<RcloneItem> { orphanFile });
        SetupFileListing(TargetId, new List<RcloneItem> { targetFile });

        // Same createTime = same conversation identity
        string sharedCreateTime = "2026-01-01T09:00:00Z";
        SetupFileContent(OrphanId, fileName, MakeJson(sharedCreateTime));
        SetupFileContent(TargetId, fileName, MakeJson(sharedCreateTime));

        // Act
        await _sut.RunAsync(_remote, new Progress<SyncProgressEvent>(), CancellationToken.None);

        // Assert
        // Step 1: Older target version must be deleted to make room for the newer orphan version.
        _mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(a => a[0] == "deletefile"),
            null, It.IsAny<CancellationToken>(), null),
            Times.Once,
            "the stale target file must be deleted before the newer orphan version is moved in");

        // Step 2: The orphan file must then be moved into the target folder.
        _mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(a => a[0] == "moveto"),
            null, It.IsAny<CancellationToken>(), null),
            Times.Once,
            "the newer orphan file must be moved to the target folder, replacing the older version");
    }

    // ─── Test 2: File in orphan NOT present in target → direct move ───────────────

    [Fact]
    public async Task RunAsync_WhenOrphanHasFilesNotInTarget_ShouldMoveThem_WithoutConflictLogic()
    {
        // Arrange
        const string newFile = "BrandNew.prompt";

        var orphanFile = new RcloneItem("f1", newFile, DateTime.UtcNow, false, AppConstants.AiStudioMimeType);

        SetupDirListing(TwoDirsListing());
        SetupFileListing(OrphanId, new List<RcloneItem> { orphanFile });
        SetupFileListing(TargetId, new List<RcloneItem>()); // target is empty

        // Act
        await _sut.RunAsync(_remote, new Progress<SyncProgressEvent>(), CancellationToken.None);

        // Assert
        // No identity inspection required – just move the file directly.
        _mockRclone.Verify(x => x.ReadFileContentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "when there is no name collision, identity inspection must NOT be performed");

        _mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(a => a[0] == "moveto"),
            null, It.IsAny<CancellationToken>(), null),
            Times.Once,
            "a file with no name collision must be moved directly to the target folder");
    }

    // ─── Test 3: Same name, DIFFERENT identity → rename orphan ────────────────────

    [Fact]
    public async Task RunAsync_WhenIdentitiesDiffer_ShouldRenameOrphanFile_ToPreventDataLoss()
    {
        // Arrange
        const string fileName = "Conversation.prompt";
        var baseTime = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        var orphanFile = new RcloneItem("f_orphan", fileName, baseTime, false, AppConstants.AiStudioMimeType);
        var targetFile = new RcloneItem("f_target", fileName, baseTime, false, AppConstants.AiStudioMimeType);

        SetupDirListing(TwoDirsListing());
        SetupFileListing(OrphanId, new List<RcloneItem> { orphanFile });
        SetupFileListing(TargetId, new List<RcloneItem> { targetFile });

        // DIFFERENT createTimes = two completely separate conversations that happen to share a name
        SetupFileContent(OrphanId, fileName, MakeJson("2026-01-10T08:00:00Z")); // different conversation
        SetupFileContent(TargetId, fileName, MakeJson("2026-02-20T14:00:00Z")); // different conversation

        // Act
        await _sut.RunAsync(_remote, new Progress<SyncProgressEvent>(), CancellationToken.None);

        // Assert
        // The orphan must be moved but under a NEW name (timestamp suffix) to avoid overwriting
        // the unrelated conversation in the target folder.
        _mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(a => a[0] == "moveto" && a[2] != $"gdrive_test,root_folder_id={TargetId}:{fileName}"),
            null, It.IsAny<CancellationToken>(), null),
            Times.Once,
            "an identity mismatch must cause the orphan to be moved with a renamed, timestamped filename");

        // The target file must NOT be deleted – it is a different conversation
        _mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(a => a[0] == "deletefile"),
            null, It.IsAny<CancellationToken>(), null),
            Times.Never,
            "a different-identity target file must never be deleted during orphan consolidation");
    }

    // ─── Test 4: CancellationToken is propagated ───────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenCancelledDuringFileLoop_ShouldThrowOperationCanceledException()
    {
        // Arrange – cancellation is requested during the per-file loop (requires a name collision)
        var cts = new CancellationTokenSource();
        const string collisionName = "Collision.prompt";

        var orphanFile = new RcloneItem("f1", collisionName, DateTime.UtcNow, false, AppConstants.AiStudioMimeType);
        var targetFile = new RcloneItem("f2", collisionName, DateTime.UtcNow, false, AppConstants.AiStudioMimeType);

        SetupDirListing(TwoDirsListing());
        SetupFileListing(OrphanId, new List<RcloneItem> { orphanFile });
        SetupFileListing(TargetId, new List<RcloneItem> { targetFile });

        // Cancel as soon as the first ReadFileContent is attempted
        _mockRclone
            .Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, string _, CancellationToken ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return await Task.FromResult(string.Empty);
            });

        // Act
        Func<Task> act = async () =>
            await _sut.RunAsync(_remote, new Progress<SyncProgressEvent>(), cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>(
            "the stage must propagate cancellation immediately rather than continuing with further work");
    }

    // ─── Test 5: Drive with no target folder in listing → no-op ──────────────────

    [Fact]
    public async Task RunAsync_WhenListingContainsNoTargetFolderAtAll_ShouldPerformNoOperations()
    {
        // Arrange – the listing returns only unrelated directories (no 'Google AI Studio' folder)
        var dirs = new List<RcloneItem>
        {
            new RcloneItem("some_id", "My Documents", DateTime.UtcNow, true, "dir"),
            new RcloneItem("other_id", "Photos",       DateTime.UtcNow, true, "dir")
        };

        _mockRclone
            .Setup(x => x.ListItemsAsync(It.Is<string>(s => s.EndsWith(":")), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirs);

        // Act
        await _sut.RunAsync(_remote, new Progress<SyncProgressEvent>(), CancellationToken.None);

        // Assert
        _mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.IsAny<string[]>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()),
            Times.Never,
            "if there is no 'Google AI Studio' folder at all, no move or delete commands should be issued");

        _mockGoogleApi.Verify(x => x.DeleteFolderIfOwnedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── Test 6: History file must never be moved ─────────────────────────────────

    [Fact]
    public async Task RunAsync_ShouldNeverMove_AppletAccessHistoryFile()
    {
        // Arrange – the orphan folder contains only the history file
        var historyFile = new RcloneItem(
            "h1",
            AppConstants.HistoryFileName,
            DateTime.UtcNow,
            false,
            "application/json");

        SetupDirListing(TwoDirsListing());
        SetupFileListing(OrphanId, new List<RcloneItem> { historyFile });
        SetupFileListing(TargetId, new List<RcloneItem>());

        // Act
        await _sut.RunAsync(_remote, new Progress<SyncProgressEvent>(), CancellationToken.None);

        // Assert
        _mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(a => a[0] == "moveto"),
            null, It.IsAny<CancellationToken>(), null),
            Times.Never,
            $"'{AppConstants.HistoryFileName}' is a per-drive metadata file and must be excluded from consolidation");
    }

    // ─── Test 7: Orphan folder is deleted after consolidation ─────────────────────

    [Fact]
    public async Task RunAsync_AfterConsolidation_ShouldAttemptToDeleteOrphanFolder()
    {
        // Arrange – orphan with a single file that doesn't exist in the target
        var orphanFile = new RcloneItem("f1", "Chat.prompt", DateTime.UtcNow, false, AppConstants.AiStudioMimeType);

        SetupDirListing(TwoDirsListing());
        SetupFileListing(OrphanId, new List<RcloneItem> { orphanFile });
        SetupFileListing(TargetId, new List<RcloneItem>());

        _mockGoogleApi
            .Setup(x => x.DeleteFolderIfOwnedAsync(RemoteName, OrphanId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await _sut.RunAsync(_remote, new Progress<SyncProgressEvent>(), CancellationToken.None);

        // Assert – after files are moved, the orphan folder must be cleaned up via the API
        _mockGoogleApi.Verify(x => x.DeleteFolderIfOwnedAsync(RemoteName, OrphanId, It.IsAny<CancellationToken>()),
            Times.Once,
            "after consolidation, the orphaned folder must be deleted to prevent it from reappearing in future syncs");
    }
}
