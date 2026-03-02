using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using Moq;
using Xunit;
using FluentAssertions;
using System.Linq;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for <see cref="MeshPermissionService"/>.
/// Validates cross-permission revocation logic and edge cases like empty mesh or missing identifiers.
/// </summary>
public class MeshPermissionServiceTests
{
    private readonly Mock<IGoogleDriveApiService> _mockGoogleApi;
    private readonly MeshPermissionService _sut;

    public MeshPermissionServiceTests()
    {
        _mockGoogleApi = new Mock<IGoogleDriveApiService>();
        _sut = new MeshPermissionService(_mockGoogleApi.Object);
    }

    // ─── Core Revocation Logic ────────────────────────────────────────────────
    
    [Fact]
    public async Task RevokeMeshPermissionsAsync_ShouldRevokeCrossPermissionsForEveryOtherNode()
    {
        // Arrange
        var targetToRemove = new RemoteInfo("Target", "target_remote", "target_folder", "target@email.com");
        var existingRemotes = new List<RemoteInfo>
        {
            new RemoteInfo("Net1", "net1_remote", "net1_folder", "net1@email.com"),
            new RemoteInfo("Net2", "net2_remote", "net2_folder", "net2@email.com")
        };

        // Act
        await _sut.RevokeMeshPermissionsAsync(targetToRemove, existingRemotes);

        // Assert
        // Verify that permissions are revoked both ways for all nodes in the mesh.
        _mockGoogleApi.Verify(x => x.RevokePermissionAsync("net1_remote", "net1_folder", "target@email.com", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.Verify(x => x.RevokePermissionAsync("target_remote", "target_folder", "net1@email.com", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.Verify(x => x.RevokePermissionAsync("net2_remote", "net2_folder", "target@email.com", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.Verify(x => x.RevokePermissionAsync("target_remote", "target_folder", "net2@email.com", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RevokeMeshPermissionsAsync_WithSingleOtherNode_ShouldRevokeBothDirections()
    {
        // Arrange
        var target = new RemoteInfo("Target", "target_r", "target_f", "target@email.com");
        var single = new RemoteInfo("OnlyNode", "only_r", "only_f", "only@email.com");

        // Act
        await _sut.RevokeMeshPermissionsAsync(target, new List<RemoteInfo> { single });

        // Assert
        _mockGoogleApi.Verify(x => x.RevokePermissionAsync("only_r", "only_f", "target@email.com", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.Verify(x => x.RevokePermissionAsync("target_r", "target_f", "only@email.com", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RevokeMeshPermissionsAsync_WithEmptyRemotesList_ShouldCallNoApi()
    {
        // Arrange
        var targetToRemove = new RemoteInfo("Target", "target", "folder", "email@test.com");
        var existingRemotes = new List<RemoteInfo>(); 

        // Act
        await _sut.RevokeMeshPermissionsAsync(targetToRemove, existingRemotes);

        // Assert
        _mockGoogleApi.VerifyNoOtherCalls();
    }

    // ─── Edge Cases & Resilience ───────────────────────────────────────────────

    [Fact]
    public async Task RevokeMeshPermissionsAsync_WhenOneNodeFails_ShouldContinueWithRemainder()
    {
        // Arrange
        var target = new RemoteInfo("Target", "t_r", "t_f", "t@e.com");
        var node1 = new RemoteInfo("N1", "n1_r", "n1_f", "n1@e.com");
        var node2 = new RemoteInfo("N2", "n2_r", "n2_f", "n2@e.com");

        _mockGoogleApi.Setup(x => x.RevokePermissionAsync("n1_r", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Exception("Node unreachable"));

        // Act & Assert
        await _sut.Awaiting(x => x.RevokeMeshPermissionsAsync(target, new List<RemoteInfo> { node1, node2 }))
            .Should().NotThrowAsync("a failure in one node should not halt the entire revocation mesh");
        
        _mockGoogleApi.Verify(x => x.RevokePermissionAsync("n2_r", "n2_f", "t@e.com", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RevokeMeshPermissionsAsync_WhenNodeHasNullEmail_ShouldFallbackToRcloneRemote()
    {
        // Arrange
        var targetToRemove = new RemoteInfo("Target", "t_r", "t_f", null); 
        var existing = new RemoteInfo("E1", "e1_r", "e1_f", null); 

        // Act
        await _sut.RevokeMeshPermissionsAsync(targetToRemove, new List<RemoteInfo> { existing });

        // Assert
        _mockGoogleApi.Verify(x => x.RevokePermissionAsync("e1_r", "e1_f", "t_r", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.Verify(x => x.RevokePermissionAsync("t_r", "t_f", "e1_r", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RevokeMeshPermissionsAsync_WhenNodeHasEmptyFolderId_ShouldNotThrow()
    {
        // Arrange
        var target = new RemoteInfo("Target", "t_r", "t_f", "t@e.com");
        var malformed = new RemoteInfo("Ghost", "g_r", string.Empty, "g@e.com");

        // Act & Assert
        await _sut.Awaiting(x => x.RevokeMeshPermissionsAsync(target, new List<RemoteInfo> { malformed }))
            .Should().NotThrowAsync("malformed folder identifiers should be handled without throwing exceptions");
    }

    [Fact]
    public async Task RevokeMeshPermissionsAsync_WhenCancelled_ShouldPropagateException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await _sut.Awaiting(x => x.RevokeMeshPermissionsAsync(
            new RemoteInfo("T", "r", "f", "e"), new List<RemoteInfo> { new RemoteInfo("E", "r", "f", "e") }, cts.Token))
            .Should().ThrowAsync<System.OperationCanceledException>();
    }
}
