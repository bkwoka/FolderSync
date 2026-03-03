using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FolderSync.Helpers;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Services.Interfaces;
using Moq;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Tests that verify the execution order of synchronization stages in <see cref="SyncEngine"/>.
///
/// The pipeline must follow a strict sequence:
///   Phase 1 (per-remote, parallel): Sanitize → Consolidate
///   Phase 2 (global, sequential):   CrossAccount
///
/// Breaking this order causes data corruption:
///   - CrossAccount before Consolidate → duplicates are distributed across all accounts
///   - Consolidate before Sanitize    → name collisions crash move operations
/// </summary>
public class SyncEngineStageOrderTests
{
    private readonly Mock<ISyncSanitizeStage>    _mockSanitize;
    private readonly Mock<ISyncConsolidateStage> _mockConsolidate;
    private readonly Mock<ISyncCrossAccountStage> _mockCrossAccount;
    private readonly Mock<ITranslationService>   _mockLocalizer;
    private readonly SyncEngine _sut;

    private readonly RemoteInfo _master;
    private readonly RemoteInfo _secondary;
    private readonly List<RemoteInfo> _twoRemotes;

    public SyncEngineStageOrderTests()
    {
        _mockSanitize    = new Mock<ISyncSanitizeStage>();
        _mockConsolidate = new Mock<ISyncConsolidateStage>();
        _mockCrossAccount = new Mock<ISyncCrossAccountStage>();
        _mockLocalizer   = new Mock<ITranslationService>();
        _mockLocalizer.Setup(l => l[It.IsAny<string>()]).Returns("MockedString");

        _sut = new SyncEngine(
            _mockSanitize.Object,
            _mockConsolidate.Object,
            _mockCrossAccount.Object,
            _mockLocalizer.Object);

        _master    = new RemoteInfo("Master", "gdrive_master", "folder_master");
        _secondary = new RemoteInfo("Secondary", "gdrive_secondary", "folder_sec");
        _twoRemotes = new List<RemoteInfo> { _master, _secondary };
    }

    // ─── CrossAccount Sequencing ─────────────────────────────────────────────────

    /// <summary>
    /// The CrossAccount stage aggregates changes and distributes them.
    /// It must run ONLY after all preparation stages across all drives have completed.
    /// </summary>
    [Fact]
    public async Task RunFullSync_CrossAccountStage_MustRunAfterAllPreparationStages()
    {
        // Arrange
        var callOrder = new List<string>();

        _mockSanitize
            .Setup(x => x.RunAsync(It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("sanitize"))
            .Returns(Task.CompletedTask);

        _mockConsolidate
            .Setup(x => x.RunAsync(It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("consolidate"))
            .Returns(Task.CompletedTask);

        _mockCrossAccount
            .Setup(x => x.RunAsync(It.IsAny<List<RemoteInfo>>(), It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<Action>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("cross_account"))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.RunFullSync(_twoRemotes, _master, new Mock<IProgress<SyncProgressEvent>>().Object, new Mock<IProgress<double>>().Object);

        // Assert
        callOrder.Should().NotBeEmpty();
        callOrder.Last().Should().Be("cross_account",
            "the cross-account stage must be the final operation to prevent propagating duplicates");
    }

    /// <summary>
    /// Verifies the plumbing: 2 sanitize + 2 consolidate + 1 cross_account = 5 calls total for 2 remotes.
    /// </summary>
    [Fact]
    public async Task RunFullSync_WithTwoRemotes_ShouldProduceFiveStageCallsInTotal()
    {
        // Arrange
        var callOrder = new List<string>();

        _mockSanitize
            .Setup(x => x.RunAsync(It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("sanitize"))
            .Returns(Task.CompletedTask);

        _mockConsolidate
            .Setup(x => x.RunAsync(It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("consolidate"))
            .Returns(Task.CompletedTask);

        _mockCrossAccount
            .Setup(x => x.RunAsync(It.IsAny<List<RemoteInfo>>(), It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<Action>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("cross_account"))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.RunFullSync(_twoRemotes, _master, new Mock<IProgress<SyncProgressEvent>>().Object, new Mock<IProgress<double>>().Object);

        // Assert
        callOrder.Should().HaveCount(5);
        callOrder.Count(c => c == "sanitize").Should().Be(2);
        callOrder.Count(c => c == "consolidate").Should().Be(2);
        callOrder.Count(c => c == "cross_account").Should().Be(1);
    }

    /// <summary>
    /// Within each remote's preparation task, Sanitize must be awaited before Consolidate.
    /// </summary>
    [Fact]
    public async Task RunFullSync_WithinEachRemoteTask_SanitizeMustPrecedeConsolidate()
    {
        // Arrange
        var masterCallOrder = new List<string>();
        var secondaryCallOrder = new List<string>();

        _mockSanitize
            .Setup(x => x.RunAsync(It.Is<RemoteInfo>(r => r == _master), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => masterCallOrder.Add("sanitize"))
            .Returns(Task.CompletedTask);

        _mockSanitize
            .Setup(x => x.RunAsync(It.Is<RemoteInfo>(r => r == _secondary), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => secondaryCallOrder.Add("sanitize"))
            .Returns(Task.CompletedTask);

        _mockConsolidate
            .Setup(x => x.RunAsync(It.Is<RemoteInfo>(r => r == _master), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => masterCallOrder.Add("consolidate"))
            .Returns(Task.CompletedTask);

        _mockConsolidate
            .Setup(x => x.RunAsync(It.Is<RemoteInfo>(r => r == _secondary), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => secondaryCallOrder.Add("consolidate"))
            .Returns(Task.CompletedTask);

        _mockCrossAccount
            .Setup(x => x.RunAsync(It.IsAny<List<RemoteInfo>>(), It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<Action>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.RunFullSync(_twoRemotes, _master, new Mock<IProgress<SyncProgressEvent>>().Object, new Mock<IProgress<double>>().Object);

        // Assert
        masterCallOrder[0].Should().Be("sanitize");
        masterCallOrder[1].Should().Be("consolidate");

        secondaryCallOrder[0].Should().Be("sanitize");
        secondaryCallOrder[1].Should().Be("consolidate");
    }

    // ─── Error Propagation ────────────────────────────────────────────────────────

    /// <summary>
    /// If preparation is cancelled, the global sync must abort before distribution starts.
    /// </summary>
    [Fact]
    public async Task RunFullSync_WhenSanitizeIsCancelled_CrossAccountMustNotRun()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        _mockSanitize
            .Setup(x => x.RunAsync(It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()))
            .Returns((RemoteInfo _, IProgress<SyncProgressEvent> _, CancellationToken ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        // Act
        Func<Task> act = () =>
            _sut.RunFullSync(_twoRemotes, _master, new Mock<IProgress<SyncProgressEvent>>().Object, new Mock<IProgress<double>>().Object, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        _mockCrossAccount.Verify(
            x => x.RunAsync(It.IsAny<List<RemoteInfo>>(), It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<Action>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
