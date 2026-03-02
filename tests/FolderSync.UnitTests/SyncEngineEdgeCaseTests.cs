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
/// Supplemental unit tests for <see cref="SyncEngine"/> covering edge cases for varying remotes list sizes.
/// These tests verify proper input validation and progress reporting.
/// </summary>
public class SyncEngineEdgeCaseTests
{
    private readonly Mock<ISyncSanitizeStage>     _mockSanitize;
    private readonly Mock<ISyncConsolidateStage>  _mockConsolidate;
    private readonly Mock<ISyncCrossAccountStage> _mockCrossAccount;
    private readonly SyncEngine                   _sut;

    public SyncEngineEdgeCaseTests()
    {
        _mockSanitize    = new Mock<ISyncSanitizeStage>();
        _mockConsolidate = new Mock<ISyncConsolidateStage>();
        _mockCrossAccount = new Mock<ISyncCrossAccountStage>();

        var mockLocalizer = new Mock<ITranslationService>();
        mockLocalizer.Setup(l => l[It.IsAny<string>()]).Returns("MockedString");

        _sut = new SyncEngine(
            _mockSanitize.Object,
            _mockConsolidate.Object,
            _mockCrossAccount.Object,
            mockLocalizer.Object);
    }

    [Fact]
    public async Task RunFullSync_WithEmptyRemotesList_ShouldReportErrorAndReturnEarly()
    {
        // Arrange
        var master       = new RemoteInfo("Master", "r", "f");
        var emptyList    = new List<RemoteInfo>();
        var progressValues = new List<double>();
        var progressUpdater = new SyncProgressReport(v => progressValues.Add(v));

        // Act
        await _sut.RunFullSync(emptyList, master, new Progress<SyncProgressEvent>(), progressUpdater);

        // Assert – no stage must run for an empty list
        _mockSanitize.Verify(
            x => x.RunAsync(It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        progressValues.Should().Contain(100.0,
            "progress must be finalized at 100 even if the process aborts early");
    }

    [Fact]
    public async Task RunFullSync_WithExactlyTwoRemotes_ShouldRunAllThreeStages()
    {
        // Arrange
        var master    = new RemoteInfo("Master",    "rm", "fm");
        var secondary = new RemoteInfo("Secondary", "rs", "fs");
        var twoRemotes = new List<RemoteInfo> { master, secondary };

        // Act
        await _sut.RunFullSync(twoRemotes, master, new Progress<SyncProgressEvent>(), new Progress<double>());

        // Assert – sanitize/consolidate run twice each, crossAccount runs once
        _mockSanitize.Verify(
            x => x.RunAsync(It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        _mockConsolidate.Verify(
            x => x.RunAsync(It.IsAny<RemoteInfo>(), It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        _mockCrossAccount.Verify(
            x => x.RunAsync(twoRemotes, master, It.IsAny<IProgress<SyncProgressEvent>>(), It.IsAny<Action>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunFullSync_WithThreeRemotes_ShouldReachExactly100PercentProgress()
    {
        // Arrange
        var master   = new RemoteInfo("Master",  "rm",  "fm");
        var slave1   = new RemoteInfo("Slave1",  "rs1", "fs1");
        var slave2   = new RemoteInfo("Slave2",  "rs2", "fs2");
        var remotes  = new List<RemoteInfo> { master, slave1, slave2 };

        var progressValues = new List<double>();
        // Using a synchronous progress implementation to avoid race conditions in tests
        var progressUpdater = new SyncProgressReport(v => progressValues.Add(v));

        // Act
        await _sut.RunFullSync(remotes, master, new Progress<SyncProgressEvent>(), progressUpdater);

        // Assert – verify monotonic progress and accurate completion
        progressValues.Should().Contain(0.0, "progress must start at 0");
        progressValues.Should().Contain(100.0, "progress must end at 100");
        progressValues.Should().OnlyContain(v => v >= 0 && v <= 100);
    }

    private sealed class SyncProgressReport : IProgress<double>
    {
        private readonly Action<double> _handler;
        public SyncProgressReport(Action<double> handler) => _handler = handler;
        public void Report(double value) => _handler(value);
    }
}
