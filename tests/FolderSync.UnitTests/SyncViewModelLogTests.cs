using FluentAssertions;
using FolderSync.Helpers;
using FolderSync.Models;
using FolderSync.Services.Interfaces;
using FolderSync.ViewModels;
using Moq;
using System;
using System.Linq;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for <see cref="SyncViewModel"/> logging logic.
/// Validates log management, retention, and Task ID based activity state updates.
/// </summary>
public class SyncViewModelLogTests
{
    private readonly SyncViewModel _sut; 

    public SyncViewModelLogTests()
    {
        var mockEngine = new Mock<ISyncEngine>();
        var mockConfig = new Mock<IConfigService>();
        var mockRclone = new Mock<IRcloneService>();
        var mockLocalizer = new Mock<ITranslationService>();
        mockLocalizer.Setup(l => l[It.IsAny<string>()]).Returns("Mock");
        
        _sut = new SyncViewModel(mockEngine.Object, mockConfig.Object, mockRclone.Object, mockLocalizer.Object);
    }

    // ─── Visual & Data Mapping ───────────────────────────────────────────────
    
    [Fact]
    public void AddLog_WithExplicitStructuredData_ShouldMapToLogEntryProperties()
    {
        // Act
        var taskId = Guid.NewGuid();
        _sut.AddLog(new SyncProgressEvent(taskId, "Processing...", false, LogEntryType.Inspect, 2));

        // Assert
        var log = _sut.Logs[0];
        log.Id.Should().Be(taskId);
        log.Type.Should().Be(LogEntryType.Inspect);
        log.IndentLevel.Should().Be(2);
        log.Text.Should().Be("Processing...");
        log.Margin.Left.Should().Be(48.0); // 2 * 24
    }

    // ─── Log Retention & Rotation ──────────────────────────────────────────────

    [Fact]
    public void AddLog_WhenLogsCountReachesLimit_ShouldRotateAndRemoveOldestEntries()
    {
        // Arrange – fill to the threshold (550)
        for (int i = 0; i < 549; i++)
            _sut.AddLog(new SyncProgressEvent(Guid.NewGuid(), $"Entry {i}", false));

        var firstEntryText = _sut.Logs[0].Text;

        // Act – adding 550th entry triggers rotation (removes 50)
        _sut.AddLog(new SyncProgressEvent(Guid.NewGuid(), "Triggering Entry", false));

        // Assert
        _sut.Logs.Should().HaveCount(500, "rotation should occur precisely at 550 entries, removing the first 50");
        _sut.Logs.Should().NotContain(l => l.Text == firstEntryText);
    }

    // ─── Formatting & Indentation ────────────────────────────────────────────

    [Fact]
    public void AddLog_WithWhitespaceMessage_ShouldAddAsEmptySeparator()
    {
        // Act
        _sut.AddLog(new SyncProgressEvent(Guid.NewGuid(), "   ", false));

        // Assert
        var entry = _sut.Logs[0];
        entry.Text.Should().BeEmpty();
    }

    // ─── Task Lifecycle State ──────────────────────────────────────────────────

    [Fact]
    public void AddLog_FullTaskSequence_ShouldUpdateActiveStatusCorrectly()
    {
        // Arrange
        var taskId = Guid.NewGuid();

        // 1. Start Task
        _sut.AddLog(new SyncProgressEvent(taskId, "Uploading...", false, LogEntryType.Upload));
        _sut.Logs.Last().IsActive.Should().BeTrue("started task must display activity status (spinner)");

        // 2. Complete Task
        _sut.AddLog(new SyncProgressEvent(taskId, "", IsFinished: true));
        
        // Assert
        var entry = _sut.Logs.First(l => l.Id == taskId);
        entry.IsActive.Should().BeFalse("finished task must deactivate its activity status");
    }
}
