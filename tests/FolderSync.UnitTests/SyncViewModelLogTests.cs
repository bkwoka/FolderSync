using FluentAssertions;
using FolderSync.Helpers;
using FolderSync.Services.Interfaces;
using FolderSync.ViewModels;
using Moq;
using System;
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
        // Verify that UI log entries are assigned the correct visual brand colors based on log content emojis.
        var log = _sut.Logs[0];
        log.IconColor.Should().BeEquivalentTo(expectedColor);
        log.HasIcon.Should().BeTrue();
    }
}
