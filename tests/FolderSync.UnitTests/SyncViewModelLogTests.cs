using FluentAssertions;
using FolderSync.Helpers;
using FolderSync.Services.Interfaces;
using FolderSync.ViewModels;
using Moq;
using System;
using System.Linq; // Added for .Last() and .First()
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for <see cref="SyncViewModel"/>.
/// Validates UI mapping, log icon coloring, and progress reporting.
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

    // ─── Visual Mapping ───────────────────────────────────────────────────────
    
    [Theory]
    [InlineData("📦 Moving", "#8899A6")]
    [InlineData("🔍 Scanning", "#0699BE")]
    [InlineData("⇄ Network", "#0699BE")]
    [InlineData("⬇ Downloading", "#6CCC3C")]
    [InlineData("⬆ Uploading", "#6CCC3C")]
    public void AddLog_WithVariousEmojis_ShouldMapToCorrectIconColor(string message, string expectedColor)
    {
        // Act
        _sut.AddLog(new SyncProgressEvent(Guid.NewGuid(), message, false));

        // Assert
        var log = _sut.Logs[0];
        log.IconColor.Should().BeEquivalentTo(expectedColor);
        log.HasIcon.Should().BeTrue();
    }

    [Fact]
    public void AddLog_WithUnrecognizedPrefix_ShouldUseDefaultColorAndHaveNoIcon()
    {
        // Act
        _sut.AddLog(new SyncProgressEvent(Guid.NewGuid(), "🚀 Rocket message", false));

        // Assert
        var entry = _sut.Logs[0];
        entry.HasIcon.Should().BeFalse();
        entry.IconColor.Should().Be("#e4eaec");
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
    public void AddLog_WithLeadingSpaces_ShouldSetMarginAndTrimText()
    {
        // Act – 4 spaces = 24px margin (4 * 6px)
        _sut.AddLog(new SyncProgressEvent(Guid.NewGuid(), "    Indented message", false));

        // Assert
        var entry = _sut.Logs[0];
        entry.LeftMargin.Should().Be(24.0);
        entry.Text.Should().Be("Indented message");
    }

    [Fact]
    public void AddLog_WithWhitespaceMessage_ShouldAddAsEmptySeparator()
    {
        // Act
        _sut.AddLog(new SyncProgressEvent(Guid.NewGuid(), "   ", false));

        // Assert
        var entry = _sut.Logs[0];
        entry.Text.Should().BeEmpty();
        entry.HasIcon.Should().BeFalse();
    }

    // ─── Task Lifecycle State ──────────────────────────────────────────────────

    [Fact]
    public void AddLog_FullTaskSequence_ShouldUpdateActiveStatusCorrectly()
    {
        // Arrange
        var taskId = Guid.NewGuid();

        // 1. Start Task
        _sut.AddLog(new SyncProgressEvent(taskId, "⬆ Uploading...", false));
        _sut.Logs.Last().IsActive.Should().BeTrue("started task must display activity status (spinner)");

        // 2. Complete Task
        _sut.AddLog(new SyncProgressEvent(taskId, "", IsFinished: true));
        
        // Assert
        var entry = _sut.Logs.First(l => l.Id == taskId);
        entry.IsActive.Should().BeFalse("finished task must deactivate its activity status");
    }
}
