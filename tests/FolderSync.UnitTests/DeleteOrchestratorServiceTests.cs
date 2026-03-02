using System;
using System.Collections.Generic;
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
    }

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
        // Verify data transformation: Ensure JSON was parsed and Google API was invoked for the attachment ID.
        _mockGoogleApi.Verify(x => x.TrashFileAsync(It.IsAny<string>(), "attach123", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        
        // Verify Hard Delete: Ensure the prompt file was deleted with trash bypassed across remotes.
        _mockRclone.Verify(x => x.ExecuteCommandAsync(
            It.Is<string[]>(args => args[0] == "deletefile" && args[2] == "--drive-use-trash=false"), 
            null, It.IsAny<CancellationToken>(), null), Times.Exactly(2)); 
    }

    [Fact]
    public async Task DeleteConversation_WithMultipleAttachments_ShouldTrashAll()
    {
        // Arrange
        string fileName = "chat.prompt";
        // Multiple attachments in varying structures
        string validJson = @"{ ""chunkedPrompt"": { ""chunks"": [ { ""file"": { ""id"": ""attach1"" } }, { ""image"": { ""id"": ""attach2"" } } ] } }";
        
        _mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(validJson);

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RcloneItem>());

        // Act
        await _sut.DeleteConversationAsync(fileName, true, _masterRemote, _allRemotes, CancellationToken.None);

        // Assert
        // All identified attachment IDs should be sent to the trash.
        _mockGoogleApi.Verify(x => x.TrashFileAsync(It.IsAny<string>(), "attach1", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _mockGoogleApi.Verify(x => x.TrashFileAsync(It.IsAny<string>(), "attach2", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DeleteConversation_WhenRcloneThrows_ShouldHandleGracefullyWithoutCrashing()
    {
        // Arrange
        // Simulate a partial failure (Scatter-Gather degradation): Master deletion logic succeeds, but Slave fails.
        _mockRclone.Setup(x => x.ExecuteCommandAsync(It.Is<string[]>(a => a[1].Contains("slave_id")), null, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .ThrowsAsync(new InvalidOperationException("API Quota Error on Slave"));

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RcloneItem> { new RcloneItem("master_id", AppConstants.TargetFolderName, DateTime.Now, true, "dir") });

        // Act
        Func<Task> act = async () => await _sut.DeleteConversationAsync("test.prompt", false, _masterRemote, _allRemotes, CancellationToken.None);

        // Assert
        // Exceptions on individual remotes should be caught and logged, preventing entire process termination.
        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ \"invalid_format\": true }")]
    [InlineData("Not a JSON")]
    public async Task DeleteConversation_WithInvalidJson_ShouldNotThrowAndSkipAttachments(string corruptedJson)
    {
        // Arrange
        _mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(corruptedJson);

        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RcloneItem>());

        // Act
        Func<Task> act = async () => await _sut.DeleteConversationAsync("test.prompt", true, _masterRemote, _allRemotes, CancellationToken.None);

        // Assert
        // The orchestrator should handle corrupted JSON gracefully without terminating the operation.
        await act.Should().NotThrowAsync();
        _mockGoogleApi.Verify(x => x.TrashFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteConversation_WithoutAttachments_ShouldSkipFileReading()
    {
        // Arrange
        _mockRclone.Setup(x => x.ListItemsAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RcloneItem>());

        // Act
        await _sut.DeleteConversationAsync("test.prompt", deleteAttachments: false, _masterRemote, _allRemotes, CancellationToken.None);

        // Assert
        // Ensure no attempt is made to read the prompt file if attachment deletion is disabled.
        _mockRclone.Verify(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockGoogleApi.Verify(x => x.TrashFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteConversation_WhenCancelled_ShouldPropagateException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel(); 

        _mockRclone.Setup(x => x.ReadFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act
        Func<Task> act = async () => await _sut.DeleteConversationAsync("test.prompt", true, _masterRemote, _allRemotes, cts.Token);

        // Assert
        // Cancellation tokens must be respected and corresponding exceptions propagated.
        await act.Should().ThrowExactlyAsync<OperationCanceledException>();
    }
}
