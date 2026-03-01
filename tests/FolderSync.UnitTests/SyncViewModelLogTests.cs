using FluentAssertions;
using FolderSync.Helpers;
using FolderSync.Services.Interfaces;
using FolderSync.ViewModels;
using Moq;
using System;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for the logging subsystem within <see cref="SyncViewModel"/>.
/// Validates correct emoji-to-icon mapping, indentation handling, and log list management.
/// </summary>
public class SyncViewModelLogTests
{
    private readonly SyncViewModel _sut; // System Under Test

    public SyncViewModelLogTests()
    {
        // Mock dependencies to isolate log transformation logic from disk or configuration access
        var mockEngine = new Mock<ISyncEngine>();
        var mockConfig = new Mock<IConfigService>();
        var mockRclone = new Mock<IRcloneService>();
        var mockLocalizer = new Mock<ITranslationService>();
        mockLocalizer.Setup(l => l[It.IsAny<string>()]).Returns((string s) => s);
        
        _sut = new SyncViewModel(mockEngine.Object, mockConfig.Object, mockRclone.Object, mockLocalizer.Object);
    }

    /// <summary>
    /// Verifies that logs containing the warning emoji are mapped to the correct visual red icon
    /// and that the emoji itself is stripped from the display text.
    /// </summary>
    [Fact]
    public void AddLog_WithWarningEmoji_ShouldMapToRedIconAndStripEmoji()
    {
        // Act
        _sut.AddLog(new SyncProgressEvent(Guid.NewGuid(), "⚠️ Processing name duplicates...", false));

        // Assert
        _sut.Logs.Should().ContainSingle();
        var log = _sut.Logs[0];
        
        log.Text.Should().Be("Processing name duplicates...");
        log.IconColor.Should().BeEquivalentTo("#E05C5C"); // Brand semantic Red
        log.HasIcon.Should().BeTrue();
    }

    /// <summary>
    /// Verifies that leading spaces in log entries are correctly translated into UI margins 
    /// for hierarchical log visualization.
    /// </summary>
    [Fact]
    public void AddLog_WithIndentation_ShouldSetLeftMargin()
    {
        // Act (Double leading space indicates sub-task)
        _sut.AddLog(new SyncProgressEvent(Guid.NewGuid(), "  📦 Moving folder contents...", false));

        // Assert
        var log = _sut.Logs[0];
        log.Text.Should().Be("Moving folder contents...");
        log.LeftMargin.Should().Be(12); // Dynamic indentation (2 spaces * 6px)
    }

    /// <summary>
    /// Verifies that whitespace-only messages are treated as empty log separators.
    /// </summary>
    [Fact]
    public void AddLog_WithEmptyString_ShouldAddEmptyLogEntry()
    {
        // Act
        _sut.AddLog(new SyncProgressEvent(Guid.NewGuid(), "   ", false));

        // Assert
        _sut.Logs.Should().ContainSingle();
        _sut.Logs[0].Text.Should().Be("");
        _sut.Logs[0].HasIcon.Should().BeFalse();
    }

    /// <summary>
    /// Verifies that newline characters at the beginning of a message are correctly
    /// converted into separate, empty log entries for visual spacing.
    /// </summary>
    [Fact]
    public void AddLog_WithNewlinePrefix_ShouldSplitIntoTwoEntries()
    {
        // Act
        _sut.AddLog(new SyncProgressEvent(Guid.NewGuid(), "\nSTAGE 2: Updating conversations...", false));

        // Assert
        _sut.Logs.Should().HaveCount(2);
        _sut.Logs[0].Text.Should().Be(""); // Visual separator
        _sut.Logs[1].Text.Should().Be("STAGE 2: Updating conversations...");
    }
}
