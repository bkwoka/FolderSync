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
/// Unit tests for <see cref="MeshPermissionService"/> focused on permission granting and rollback logic.
/// These tests verify the Saga pattern implementation to prevent "Split-Brain" drive states.
/// </summary>
public class MeshPermissionGrantTests
{
    private readonly Mock<IGoogleDriveApiService> _mockGoogleApi;
    private readonly MeshPermissionService _sut;

    public MeshPermissionGrantTests()
    {
        _mockGoogleApi = new Mock<IGoogleDriveApiService>();
        _sut = new MeshPermissionService(_mockGoogleApi.Object);
    }

    [Fact]
    public async Task GrantMeshPermissions_WithTwoExistingNodes_ShouldShareBothDirectionsForEach()
    {
        // Arrange
        var newRemote   = new RemoteInfo("New",   "new_r",  "new_f",  "new@e.com");
        var existing1   = new RemoteInfo("E1",    "e1_r",   "e1_f",   "e1@e.com");
        var existing2   = new RemoteInfo("E2",    "e2_r",   "e2_f",   "e2@e.com");
        var existingList = new List<RemoteInfo> { existing1, existing2 };

        // Act
        await _sut.GrantMeshPermissionsAsync(newRemote, existingList);

        // Assert – Bi-directional sharing for each remote
        _mockGoogleApi.Verify(x => x.ShareFolderAsync("new_r",  "new_f",  "e1@e.com", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.Verify(x => x.ShareFolderAsync("e1_r",  "e1_f",   "new@e.com", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.Verify(x => x.ShareFolderAsync("new_r",  "new_f",  "e2@e.com", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.Verify(x => x.ShareFolderAsync("e2_r",  "e2_f",   "new@e.com", It.IsAny<CancellationToken>()), Times.Once);

        _mockGoogleApi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GrantMeshPermissions_WhenEmailIsNull_ShouldUseRcloneRemoteAsIdentifier()
    {
        // Arrange
        var newRemote  = new RemoteInfo("New", "new_r", "new_f", null);
        var existing   = new RemoteInfo("E1",  "e1_r",  "e1_f",  null);

        // Act
        await _sut.GrantMeshPermissionsAsync(newRemote, new List<RemoteInfo> { existing });

        // Assert – fallback to RcloneRemote when Email is unavailable
        _mockGoogleApi.Verify(x => x.ShareFolderAsync("new_r", "new_f", "e1_r", It.IsAny<CancellationToken>()), Times.Once);
        _mockGoogleApi.Verify(x => x.ShareFolderAsync("e1_r",  "e1_f",  "new_r", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GrantMeshPermissions_WhenShareFailsPartway_ShouldRollbackAllGrantedPermissions()
    {
        // Arrange
        var newRemote  = new RemoteInfo("New", "new_r", "new_f", "new@e.com");
        var existing1  = new RemoteInfo("E1",  "e1_r",  "e1_f",  "e1@e.com");
        var existing2  = new RemoteInfo("E2",  "e2_r",  "e2_f",  "e2@e.com");

        int shareCallCount = 0;
        _mockGoogleApi
            .Setup(x => x.ShareFolderAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() =>
            {
                if (++shareCallCount == 3)
                    throw new InvalidOperationException("API failure");
            });

        // Act
        Func<Task> act = async () =>
            await _sut.GrantMeshPermissionsAsync(newRemote, new List<RemoteInfo> { existing1, existing2 });

        // Assert – exception must propagate and previous gains must be revoked
        await act.Should().ThrowAsync<InvalidOperationException>();

        _mockGoogleApi.Verify(
            x => x.RevokePermissionAsync("new_r", "new_f", "e1@e.com", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce, "successful previous steps must be revoked upon failure");

        _mockGoogleApi.Verify(
            x => x.RevokePermissionAsync("e1_r", "e1_f", "new@e.com", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GrantMeshPermissions_WhenRollbackFails_ShouldContinueRevoking()
    {
        // Arrange
        var newRemote = new RemoteInfo("New", "new_r", "new_f", "new@e.com");
        var existing  = new RemoteInfo("E1",  "e1_r",  "e1_f",  "e1@e.com");

        _mockGoogleApi
            .Setup(x => x.ShareFolderAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, string _, CancellationToken _) => {
                await Task.CompletedTask;
                throw new Exception("Share failure");
            });

        _mockGoogleApi
            .Setup(x => x.RevokePermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Rollback error"));

        // Act & Assert – share failure exception must lead to best-effort rollback
        Func<Task> act = async () => await _sut.GrantMeshPermissionsAsync(newRemote, new List<RemoteInfo> { existing });
        await act.Should().ThrowAsync<Exception>("original share error must propagate regardless of rollback status");
    }

    [Fact]
    public async Task GrantMeshPermissions_WhenCancelledBeforeStart_ShouldThrowWithoutCallingApi()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var newRemote = new RemoteInfo("New", "new_r", "new_f", "new@e.com");

        // Act & Assert
        await _sut.Awaiting(x => x.GrantMeshPermissionsAsync(newRemote, new List<RemoteInfo>(), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        _mockGoogleApi.VerifyNoOtherCalls();
    }
}
