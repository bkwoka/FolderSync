using System;
using FluentAssertions;
using FolderSync.Services;
using FolderSync.Models;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for the <see cref="RcloneItem"/> domain model.
/// </summary>
public class RcloneItemTests
{
    /// <summary>
    /// Verifies that <see cref="RcloneItem.IsConversation"/> correctly identifies AI Studio prompt files
    /// based on their specific MIME type, including case-insensitivity handling.
    /// </summary>
    [Theory]
    [InlineData("application/vnd.google-makersuite.prompt", true)]
    [InlineData("APPLICATION/VND.GOOGLE-MAKERSUITE.PROMPT", true)]
    [InlineData("application/json", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("text/plain", false)]
    public void IsConversation_ShouldReturnCorrectValue_BasedOnMimeType(string? mimeType, bool expected)
    {
        // Arrange
        var item = new RcloneItem("123", "MyChat", DateTime.Now, false, mimeType!);

        // Act
        var result = item.IsConversation;

        // Assert
        result.Should().Be(expected);
    }
}
