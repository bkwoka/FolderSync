using System;
using System.Collections.Generic;
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
/// Unit tests for <see cref="SyncEngine"/>.
/// 
/// The engine orchestrates three stages: Sanitize → Consolidate → CrossAccount.
/// These tests verify guard clauses, stage invocation order, progress reporting,
/// error propagation, and cancellation behaviour – all of which are absent from
/// the existing test suite.
/// </summary>
public class SyncEngineTests
{
    private readonly Mock<ISyncSanitizeStage>    _mockSanitize;
    private readonly Mock<ISyncConsolidateStage> _mockConsolidate;
    private readonly Mock<ISyncCrossAccountStage> _mockCrossAccount;
    private readonly Mock<ITranslationService>   _mockLocalizer;
    private readonly SyncEngine _sut;

    private readonly RemoteInfo _master;
    private readonly RemoteInfo _secondary;
    private readonly List<RemoteInfo> _twoRemotes;

    public SyncEngineTests()
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

    // ─── Guard clauses ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunFullSync_WithLessThanTwoRemotes_ShouldReportErrorAndReturnEarly()
    {
        // Arrange
        var mockUiLogger = new Mock<IProgress<SyncProgressEvent>>();
        var mockProgress = new Mock<IProgress<double>>();
        
        var singleRemote = new List<RemoteInfo> { _master };

        // Act
        await _sut.RunFullSync(singleRemote, _master, mockUiLogger.Object, mockProgress.Object);

        // Assert
        // No sync stages should have been invoked
        _mockSanitize.Verify(x => x.RunAsync(It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never, "sanitize stage must not run when there are fewer than 2 remotes");
        _mockConsolidate.Verify(x => x.RunAsync(It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never, "consolidate stage must not run when there are fewer than 2 remotes");
        _mockCrossAccount.Verify(x => x.RunAsync(It.IsAny<List<RemoteInfo>>(), It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<Action>(), It.IsAny<CancellationToken>()),
            Times.Never, "cross-account stage must not run when there are fewer than 2 remotes");

        // Progress must be reported as 100 (complete / skipped)
        mockProgress.Verify(x => x.Report(100), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunFullSync_WithNullRemotesList_ShouldReportErrorAndReturnEarly()
    {
        // Arrange
        var mockProgress = new Mock<IProgress<double>>();

        // Act
        Func<Task> act = async () =>
            await _sut.RunFullSync(null!, _master, new Progress<SyncProgressEvent>(), mockProgress.Object);

        await act.Should().NotThrowAsync("a null remotes list must be handled gracefully");
        mockProgress.Verify(x => x.Report(100.0), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunFullSync_WhenMasterIsNotInRemotesList_ShouldThrowArgumentException()
    {
        // Arrange
        var outsideMaster = new RemoteInfo("Ghost", "gdrive_ghost", "folder_ghost");

        // Act
        Func<Task> act = async () =>
            await _sut.RunFullSync(_twoRemotes, outsideMaster,
                new Mock<IProgress<SyncProgressEvent>>().Object, new Mock<IProgress<double>>().Object);

        // Assert
        await act.Should().ThrowExactlyAsync<ArgumentException>(
            "the master remote must always be a member of the remotes list");
    }

    // ─── Stage invocation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunFullSync_WithValidInputs_ShouldInvokeSanitizeAndConsolidateForEveryRemote()
    {
        // Arrange
        // Act
        await _sut.RunFullSync(_twoRemotes, _master,
            new Mock<IProgress<SyncProgressEvent>>().Object, new Mock<IProgress<double>>().Object);

        // Assert – both stages must run once per remote (2 remotes = 2 invocations each)
        _mockSanitize.Verify(x => x.RunAsync(
            It.IsAny<RemoteInfo>(),
            It.IsAny<IProgress<SyncProgressEvent>>(),
            It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "sanitize stage must run for every remote in the list");

        _mockConsolidate.Verify(x => x.RunAsync(
            It.IsAny<RemoteInfo>(),
            It.IsAny<IProgress<SyncProgressEvent>>(),
            It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "consolidate stage must run for every remote in the list");
    }

    [Fact]
    public async Task RunFullSync_WithValidInputs_ShouldInvokeCrossAccountStageOnce()
    {
        // Act
        await _sut.RunFullSync(_twoRemotes, _master,
            new Mock<IProgress<SyncProgressEvent>>().Object, new Mock<IProgress<double>>().Object);

        // Assert – CrossAccount runs once for the entire remote set, not per-remote
        _mockCrossAccount.Verify(x => x.RunAsync(
            _twoRemotes,
            _master,
            It.IsAny<IProgress<SyncProgressEvent>>(),
            It.IsAny<Action>(),
            It.IsAny<CancellationToken>()),
            Times.Once,
            "the cross-account stage is a single operation over all remotes and must run exactly once");
    }

    // ─── Progress reporting ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunFullSync_ShouldReportProgressStartingAt0_AndEndingAt100()
    {
        // Arrange
        var mockProgress = new Mock<IProgress<double>>();

        // Act
        await _sut.RunFullSync(_twoRemotes, _master, new Mock<IProgress<SyncProgressEvent>>().Object, mockProgress.Object);

        // Assert
        // The progress must restart at 0 and conclude precisely at 100 on successful completion.
        mockProgress.Verify(x => x.Report(0.0), Times.AtLeastOnce, "Progress must be reset to 0 at the start of synchronization.");
        mockProgress.Verify(x => x.Report(100.0), Times.AtLeastOnce, "Progress must be finalized at 100% upon successful completion.");
    }

    // ─── Error propagation ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunFullSync_WhenSanitizeStageFails_ShouldPropagateException()
    {
        // Arrange – one stage failure must bubble up to the caller
        _mockSanitize
            .Setup(x => x.RunAsync(It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Rclone binary not found"));

        // Act
        Func<Task> act = async () =>
            await _sut.RunFullSync(_twoRemotes, _master,
                new Mock<IProgress<SyncProgressEvent>>().Object, new Mock<IProgress<double>>().Object);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>(
            "unexpected stage failures must propagate to the caller so the ViewModel can show an error");
    }

    // ─── Cancellation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunFullSync_WhenCancelled_ShouldThrowOperationCanceledException_AndNotInvokeCrossAccount()
    {
        // Arrange – cancel as soon as the sanitize stage starts
        var cts = new CancellationTokenSource();

        _mockSanitize
            .Setup(x => x.RunAsync(It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()))
            .Returns(async (RemoteInfo _, IProgress<SyncProgressEvent> _, CancellationToken ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                await Task.CompletedTask;
            });

        // Act
        Func<Task> act = async () =>
            await _sut.RunFullSync(_twoRemotes, _master,
                new Mock<IProgress<SyncProgressEvent>>().Object, new Mock<IProgress<double>>().Object, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation must be propagated immediately and not silently swallowed");

        _mockCrossAccount.Verify(x => x.RunAsync(
            It.IsAny<List<RemoteInfo>>(), It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(),
            It.IsAny<Action>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the cross-account stage must never run after a cancellation – it would produce partial sync");
    }

    [Fact]
    public async Task RunFullSync_WhenAlreadyCancelled_ShouldThrowWithoutCallingAnyStage()
    {
        // Arrange – token is cancelled before RunFullSync is even called
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var mockProgress = new Mock<IProgress<double>>();

        // Act
        Func<Task> act = async () =>
            await _sut.RunFullSync(_twoRemotes, _master,
                new Mock<IProgress<SyncProgressEvent>>().Object, mockProgress.Object, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();

        _mockSanitize.Verify(x => x.RunAsync(
            It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a pre-cancelled token must abort the sync before any stage is invoked");
    }
}
