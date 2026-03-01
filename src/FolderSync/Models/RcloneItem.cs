using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FolderSync.Models;

/// <summary>
/// Represents a file or directory item returned from Rclone.
/// </summary>
public record RcloneItem(string Id, string Name, DateTime ModTime, bool IsDir, string? MimeType)
{
    /// <summary>
    /// Gets a value indicating whether this item represents a Google AI Studio conversation.
    /// </summary>
    public bool IsConversation => MimeType != null &&
                                  MimeType.Equals(AppConstants.AiStudioMimeType, StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<RcloneItem>))]
internal partial class RcloneJsonContext : JsonSerializerContext
{
}