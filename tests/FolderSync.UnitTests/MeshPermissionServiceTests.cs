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
/// Ensures that folder sharing permissions are correctly managed between mesh nodes.
/// </summary>
public class MeshPermissionServiceTests
{
    /// <summary>
    /// Verifies that <see cref="MeshPermissionService.RevokeMeshPermissionsAsync"/> 
    /// correctly revokes bidirectional permissions between the target node and all remaining mesh nodes.
    /// </summary>
    [Fact]
    public async Task RevokeMeshPermissionsAsync_ShouldRevokeCrossPermissionsForEveryOtherNode()
    {
        // Arrange
        var mockGoogleApi = new Mock<IGoogleDriveApiService>();
        var service = new MeshPermissionService(mockGoogleApi.Object);

        var targetToRemove = new RemoteInfo("Dysk do usuniecia", "target_remote", "target_folder", "target@email.com");
        
        var existingRemotes = new List<RemoteInfo>
        {
            new("Siec 1", "net1_remote", "net1_folder", "net1@email.com"),
            new("Siec 2", "net2_remote", "net2_folder", "net2@email.com")
        };

        // Act
        await service.RevokeMeshPermissionsAsync(targetToRemove, existingRemotes);

        // Assert
        // Node 1 revokes Target, Target revokes Node 1
        mockGoogleApi.Verify(x => x.RevokePermissionAsync("net1_remote", "net1_folder", "target@email.com", It.IsAny<CancellationToken>()), Times.Once);
        mockGoogleApi.Verify(x => x.RevokePermissionAsync("target_remote", "target_folder", "net1@email.com", It.IsAny<CancellationToken>()), Times.Once);

        // Node 2 revokes Target, Target revokes Node 2
        mockGoogleApi.Verify(x => x.RevokePermissionAsync("net2_remote", "net2_folder", "target@email.com", It.IsAny<CancellationToken>()), Times.Once);
        mockGoogleApi.Verify(x => x.RevokePermissionAsync("target_remote", "target_folder", "net2@email.com", It.IsAny<CancellationToken>()), Times.Once);

        // Total: 4 API calls expected for 3-node mesh reconfiguration
        mockGoogleApi.VerifyNoOtherCalls();
    }
}
