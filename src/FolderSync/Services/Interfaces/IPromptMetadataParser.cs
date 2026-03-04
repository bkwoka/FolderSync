using System.Collections.Generic;

namespace FolderSync.Services.Interfaces;

/// <summary>
/// Domain service responsible for parsing Google AI Studio .prompt JSON files
/// to extract metadata required for synchronization and cleanup operations.
/// </summary>
public interface IPromptMetadataParser
{
    string? ExtractCreateTime(string jsonContent);
    List<string> ExtractAttachmentIds(string jsonContent);
}
