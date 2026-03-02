using System;
using System.Collections.Generic;
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
/// Unit tests for <see cref="DeleteOrchestratorService"/>.
/// Validates conversation deletion, attachment extraction from JSON, and resilience across multiple remotes.
/// </summary>
public class DeleteOrchestratorServiceTests
{
    private readonly Mock<IRcloneService> _mockRclone;
    private readonly Mock<IGoogleDriveApiService> _mockGoogleApi;
    private readonly DeleteOrchestratorService _sut; // System Under Test
    private readonly RemoteInfo _masterRemote;
    private readonly List<RemoteInfo> _allRemotes;

    public DeleteOrchestratorServiceTests()
    {
        _mockRclone = new Mock<IRcloneService>();
        _mockGoogleApi = new Mock<IGoogleDriveApiService>();
        _sut = new DeleteOrchestratorService(_mockRclone.Object, _mockGoogleApi.Object);
        
        _masterRemote = new RemoteInfo("Master", "gdrive_master", "master_id");
        _allRemotes = new List<RemoteInfo>
        {
            _masterRemote,
            new RemoteInfo("Slave", "gdrive_slave", "slave_id")
        };

        // Default setup: ListItemsAsync returns empty list (no subfolders to scan)
        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RcloneItem>());
    }

    // ─── Basic Deletion & Attachments ──────────────────────────────────────────
    
    [Fact]
    public async Task DeleteConversation_WithAttachments_ShouldExtractIdsAndCallTrash()
    {
        // Arrange
        string fileName = "chat.prompt";
        string validJson = @"{ ""chunkedPrompt"": { ""chunks"": [ { ""file"": { ""id"": ""attach123"" } } ] } }";
        
        _mockRclone.Setup(x => x.ReadFileContentAsync(_masterRemote.RcloneRemote, _masterRemote.FolderId, fileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(validJson);

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RcloneItem>());

        // Act
        await _sut.DeleteConversationAsync(fileName, deleteAttachments: true, _masterRemote, _allRemotes, CancellationToken.None);

        // Assert
        // Verify that the attachment ID was extracted and trash was invoked.
        _mockGoogleApi.Verify(x => x.TrashFileAsync(It.IsAny<string>(), "attach123", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        
        // Ensure hard delete (--drive-use-trash=false) was utilized for the prompt file itself.
        _mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(args => args[0] == "deletefile" && Array.Exists(args, arg => arg == "--drive-use-trash=false")), 
            null, It.IsAny<CancellationToken>(), null), Times.Exactly(2)); 
    }

    [Fact]
    public async Task DeleteConversation_WithDuplicateAttachmentIds_ShouldTrashEachIdOnlyOnce()
    {
        // Arrange – the same ID appears in multiple chunks (e.g., image and file references)
        const string duplicateId = "attach_duplicated";
        string json = $@"{{ ""chunkedPrompt"": {{ ""chunks"": [ {{ ""file"": {{ ""id"": ""{duplicateId}"" }} }}, {{ ""image"": {{ ""id"": ""{duplicateId}"" }} }} ] }} }}";

        _mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);

        // Act
        await _sut.DeleteConversationAsync("chat.prompt", true, _masterRemote, _allRemotes, CancellationToken.None);

        // Assert
        _mockGoogleApi.Verify(x => x.TrashFileAsync(It.IsAny<string>(), duplicateId, It.IsAny<CancellationToken>()), 
            Times.Once, "extracted IDs must be de-duplicated before calling the Google API");
    }

    [Fact]
    public async Task DeleteConversation_WhenAttachmentIdIsEmptyString_ShouldNotCallTrash()
    {
        // Arrange – malformed chunk with an empty ID string
        const string json = @"{ ""chunkedPrompt"": { ""chunks"": [ { ""file"": { ""id"": """" } } ] } }";
        _mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);

        // Act
        await _sut.DeleteConversationAsync("chat.prompt", true, _masterRemote, _allRemotes, CancellationToken.None);

        // Assert
        _mockGoogleApi.Verify(x => x.TrashFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), 
            Times.Never, "empty attachment IDs must be ignored to avoid invalid API calls");
    }

    // ─── Resilience & Error Handling ──────────────────────────────────────────

    [Fact]
    public async Task DeleteConversation_WhenReadFileThrowsNetworkException_ShouldNotAbortDeletionCycle()
    {
        // Arrange – simulate network failure during JSON retrieval
        _mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network timeout"));

        // Act & Assert
        await _sut.Awaiting(x => x.DeleteConversationAsync("chat.prompt", true, _masterRemote, _allRemotes, CancellationToken.None))
            .Should().NotThrowAsync("failure to read attachment metadata must not prevent the deletion of the conversation itself");
        
        _mockGoogleApi.Verify(x => x.TrashFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteConversation_WhenAllRemotesFail_ShouldThrowPartialDeletionException()
    {
        // Arrange
        _mockRclone.Setup(x => x.ExecuteCommandAsync(It.IsAny<string[]>(), null, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .ThrowsAsync(new InvalidOperationException("Fatal error across all accounts"));

        // Act & Assert
        await _sut.Awaiting(x => x.DeleteConversationAsync("test.prompt", false, _masterRemote, _allRemotes, CancellationToken.None))
            .Should().ThrowAsync<PartialDeletionException>("exceptions encountered during prompt file deletion must be collected and thrown as a partial failure");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("Not a JSON")]
    public async Task DeleteConversation_WithInvalidJson_ShouldGracefullySkipAttachments(string corruptedJson)
    {
        // Arrange
        _mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(corruptedJson);

        // Act & Assert
        await _sut.Awaiting(x => x.DeleteConversationAsync("test.prompt", true, _masterRemote, _allRemotes, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    // ─── Lifecycle & Cancellation ──────────────────────────────────────────────

    [Fact]
    public async Task DeleteConversation_WithSingleRemote_ShouldCompleteNormally()
    {
        // Arrange
        var singleRemoteList = new List<RemoteInfo> { _masterRemote };

        // Act & Assert
        await _sut.Awaiting(x => x.DeleteConversationAsync("chat.prompt", false, _masterRemote, singleRemoteList, CancellationToken.None))
            .Should().NotThrowAsync("the orchestrator must support single-remote configurations");
    }

    [Fact]
    public async Task DeleteConversation_WhenCancelled_ShouldPropagateException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel(); 

        // Act & Assert
        await _sut.Awaiting(x => x.DeleteConversationAsync("test.prompt", true, _masterRemote, _allRemotes, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
