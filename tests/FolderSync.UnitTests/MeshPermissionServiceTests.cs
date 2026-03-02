using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using Moq;
using Xunit;

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

    [Fact]
    public async Task RevokeMeshPermissionsAsync_ShouldRevokeCrossPermissionsForEveryOtherNode()
    {
        // Arrange
        var targetToRemove = new RemoteInfo("Dysk do usuniecia", "target_remote", "target_folder", "target@email.com");
        var existingRemotes = new List<RemoteInfo>
        {
            new RemoteInfo("Siec 1", "net1_remote", "net1_folder", "net1@email.com"),
            new RemoteInfo("Siec 2", "net2_remote", "net2_folder", "net2@email.com")
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
    public async Task RevokeMeshPermissionsAsync_WithEmptyRemotesList_ShouldCallNoApi()
    {
        // Arrange
        var targetToRemove = new RemoteInfo("Target", "target", "folder", "email@test.com");
        var existingRemotes = new List<RemoteInfo>(); 

        // Act
        await _sut.RevokeMeshPermissionsAsync(targetToRemove, existingRemotes);

        // Assert
        // Operation should gracefully skip if the mesh is empty.
        _mockGoogleApi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RevokeMeshPermissionsAsync_WhenNodeHasNullEmail_ShouldFallbackToRcloneRemote()
    {
        // Arrange
        var targetToRemove = new RemoteInfo("Target", "target_remote", "target_folder", null); 
        var existingRemotes = new List<RemoteInfo>
        {
            new RemoteInfo("Existing", "existing_remote", "existing_folder", null) 
        };

        // Act
        await _sut.RevokeMeshPermissionsAsync(targetToRemove, existingRemotes);

        // Assert
        // When email is missing, the service should fallback to using the Rclone remote name as an identifier.
        _mockGoogleApi.Verify(x => x.RevokePermissionAsync("existing_remote", "existing_folder", "target_remote", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.Verify(x => x.RevokePermissionAsync("target_remote", "target_folder", "existing_remote", It.IsAny<CancellationToken>()), Times.Once);
    }
}
