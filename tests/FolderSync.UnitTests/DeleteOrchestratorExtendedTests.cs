using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FolderSync.Exceptions;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using Moq;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Extended unit tests for <see cref="DeleteOrchestratorService"/> covering attachment extraction,
/// multi-account deletion retry logic, and cross-remote deletion propagation.
/// </summary>
public class DeleteOrchestratorExtendedTests
{
    private readonly Mock<IRcloneService>        _mockRclone;
    private readonly Mock<IGoogleDriveApiService> _mockGoogleApi;
    private readonly DeleteOrchestratorService   _sut;

    private readonly RemoteInfo _master;
    private readonly RemoteInfo _slave1;
    private readonly RemoteInfo _slave2;
    private readonly List<RemoteInfo> _allRemotes;

    private const string ValidPromptJson = """
        {
          "chunkedPrompt": {
            "chunks": [
              {
                "inlineData": { "id": "attach_001" }
              },
              {
                "fileData":   { "id": "attach_002" }
              },
              {
                "text": "some message without attachment"
              }
            ]
          }
        }
        """;

    public DeleteOrchestratorExtendedTests()
    {
        _mockRclone    = new Mock<IRcloneService>();
        _mockGoogleApi = new Mock<IGoogleDriveApiService>();
        _sut           = new DeleteOrchestratorService(_mockRclone.Object, _mockGoogleApi.Object, new PromptMetadataParser());

        _master    = new RemoteInfo("Master",  "rm",  "fm", "master@e.com");
        _slave1    = new RemoteInfo("Slave1",  "rs1", "fs1", "s1@e.com");
        _slave2    = new RemoteInfo("Slave2",  "rs2", "fs2", "s2@e.com");
        _allRemotes = new List<RemoteInfo> { _master, _slave1, _slave2 };

        _mockRclone
            .Setup(x => x.ListItemsAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RcloneItem>());
    }

    [Fact]
    public async Task DeleteConversation_WhenDeleteAttachmentsTrue_ShouldReadFileAndTrashAttachments()
    {
        // Arrange
        _mockRclone
            .Setup(x => x.ReadFileContentAsync("rm", "fm", "chat.prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ValidPromptJson);

        // Act
        await _sut.DeleteConversationAsync("chat.prompt", true, _master, _allRemotes, CancellationToken.None);

        // Assert
        _mockRclone.Verify(
            x => x.ReadFileContentAsync("rm", "fm", "chat.prompt", It.IsAny<CancellationToken>()),
            Times.Once,
            "the .prompt file must be read to extract attachment IDs");

        _mockGoogleApi.Verify(
            x => x.TrashFileAsync(It.IsAny<string>(), "attach_001", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        _mockGoogleApi.Verify(
            x => x.TrashFileAsync(It.IsAny<string>(), "attach_002", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task DeleteConversation_TokenRoulette_WhenFirstAccountUnauthorized_ShouldTryNextAccount()
    {
        // Arrange
        _mockRclone
            .Setup(x => x.ReadFileContentAsync("rm", "fm", "file.prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ValidPromptJson);

        _mockGoogleApi
            .Setup(x => x.TrashFileAsync("rm", "attach_001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("Not the owner"));

        _mockGoogleApi
            .Setup(x => x.TrashFileAsync("rs1", "attach_001", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.DeleteConversationAsync("file.prompt", true, _master, _allRemotes, CancellationToken.None);

        // Assert
        _mockGoogleApi.Verify(x => x.TrashFileAsync("rm",  "attach_001", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.Verify(x => x.TrashFileAsync("rs1", "attach_001", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.Verify(x => x.TrashFileAsync("rs2", "attach_001", It.IsAny<CancellationToken>()), Times.Never,
            "the deletion loop must terminate once an account successfully processes the request");
    }

    [Fact]
    public async Task DeleteConversation_ShouldHardDeleteFileFromAllRemotes()
    {
        // Act
        await _sut.DeleteConversationAsync("ghost.prompt", false, _master, _allRemotes, CancellationToken.None);

        // Assert
        _mockRclone.Verify(
            x => x.ExecuteCommandAsync(
                It.Is<string[]>(a => a[0] == "deletefile" && a[1].Contains("fs1")),
                null, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()),
            Times.Once,
            "the file must be deleted from all secondary drives to prevent synchronization conflicts");

        _mockRclone.Verify(
            x => x.ExecuteCommandAsync(
                It.Is<string[]>(a => a[0] == "deletefile" && a[1].Contains("fs2")),
                null, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteConversation_WhenJsonReadFails_ShouldStillDeletePromptFile()
    {
        // Arrange
        _mockRclone
            .Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Remote access error"));

        // Act
        await _sut.DeleteConversationAsync("missing.prompt", true, _master, _allRemotes, CancellationToken.None);

        // Assert
        _mockRclone.Verify(
            x => x.ExecuteCommandAsync(
                It.Is<string[]>(a => a[0] == "deletefile"),
                null, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()),
            Times.AtLeastOnce,
            "prompt file deletion must proceed even if attachment extraction fails");
    }

    [Fact]
    public async Task DeleteConversation_WhenOneSlaveDeleteFails_ShouldThrowPartialDeletionException()
    {
        // Arrange
        _mockRclone
            .Setup(x => x.ExecuteCommandAsync(
                It.Is<string[]>(a => a[0] == "deletefile" && a[1].Contains("fs1")),
                null, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .ThrowsAsync(new HttpRequestException("Slave unreachable"));

        // Act & Assert
        var act = async () => await _sut.DeleteConversationAsync("file.prompt", false, _master, _allRemotes, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PartialDeletionException>();
        ex.Which.FailedRemotes.Should().Contain("Slave1");

        _mockRclone.Verify(
            x => x.ExecuteCommandAsync(
                It.Is<string[]>(a => a[0] == "deletefile" && a[1].Contains("fs2")),
                null, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()),
            Times.Once,
            "failure on one slave must NOT stop the propagation to other slaves, even if we throw at the end");
    }

    [Fact]
    public async Task DeleteConversation_WhenFileNotFound_ShouldBeIdempotentAndNotThrow()
    {
        // Arrange - Rclone returns "not found" error
        _mockRclone
            .Setup(x => x.ExecuteCommandAsync(It.IsAny<string[]>(), null, It.IsAny<CancellationToken>(), null))
            .ThrowsAsync(new InvalidOperationException("Rclone: file not found (404)"));

        // Act & Assert
        await _sut.Awaiting(x => x.DeleteConversationAsync("ghost.prompt", false, _master, _allRemotes, CancellationToken.None))
            .Should().NotThrowAsync("idempotency: if the file is already gone, it is considered a success");
    }

    [Fact]
    public async Task DeleteConversation_WhenCancelledDuringAttachmentPhase_ShouldThrowAndStopProcessing()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        _mockRclone
            .Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ValidPromptJson);

        _mockGoogleApi
            .Setup(x => x.TrashFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, CancellationToken ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                await Task.CompletedTask;
            });

        // Act & Assert
        await _sut.Awaiting(x => x.DeleteConversationAsync("file.prompt", true, _master, _allRemotes, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DeleteConversation_WhenJsonContainsDuplicateAttachmentIds_ShouldTrashEachIdOnlyOnce()
    {
        // Arrange
        const string jsonWithDuplicates = """
            {
              "chunkedPrompt": {
                "chunks": [
                  { "file1": { "id": "shared_id" } },
                  { "file2": { "id": "shared_id" } }
                ]
              }
            }
            """;

        _mockRclone
            .Setup(x => x.ReadFileContentAsync("rm", "fm", "dupe.prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(jsonWithDuplicates);

        // Act
        await _sut.DeleteConversationAsync("dupe.prompt", true, _master, _allRemotes, CancellationToken.None);

        // Assert
        _mockGoogleApi.Verify(
            x => x.TrashFileAsync(It.IsAny<string>(), "shared_id", It.IsAny<CancellationToken>()),
            Times.Once,
            "duplicate attachment IDs must be filtered to avoid redundant API calls");
    }
}
