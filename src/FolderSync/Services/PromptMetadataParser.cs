using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services;

public class PromptMetadataParser : IPromptMetadataParser
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public string? ExtractCreateTime(string jsonContent)
    {
        if (string.IsNullOrWhiteSpace(jsonContent)) return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            if (doc.RootElement.TryGetProperty("chunkedPrompt", out var chunkedPrompt) && 
                chunkedPrompt.TryGetProperty("chunks", out var chunks) && 
                chunks.ValueKind == JsonValueKind.Array)
            {
                foreach (var chunk in chunks.EnumerateArray())
                {
                    if (chunk.TryGetProperty("createTime", out var timeElement)) 
                    {
                        return timeElement.GetString();
                    }
                }
            }
        }
        catch (JsonException ex) 
        { 
            Logger.Warn(ex, "Failed to parse JSON for createTime extraction."); 
        }
        return null;
    }

    public List<string> ExtractAttachmentIds(string jsonContent)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(jsonContent)) return ids;
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            if (doc.RootElement.TryGetProperty("chunkedPrompt", out var chunkedPrompt) &&
                chunkedPrompt.TryGetProperty("chunks", out var chunks) &&
                chunks.ValueKind == JsonValueKind.Array)
            {
                foreach (var chunk in chunks.EnumerateArray())
                {
                    foreach (var prop in chunk.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object &&
                            prop.Value.TryGetProperty("id", out var idElement) &&
                            idElement.ValueKind == JsonValueKind.String)
                        {
                            string id = idElement.GetString()!;
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                ids.Add(id);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to parse JSON structure for attachment IDs.");
        }

        return ids.Distinct().ToList();
    }
}
