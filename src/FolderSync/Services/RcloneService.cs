using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;
using FolderSync.Services.Interfaces;

namespace FolderSync.Services;

/// <summary>
/// Domain service acting as a facade for Rclone operations.
/// Delegates raw process execution and config management to specialized infrastructure classes.
/// </summary>
public partial class RcloneService : IRcloneService
{
    private readonly IRcloneProcessRunner _processRunner;
    private readonly IRcloneConfigManager _configManager;

    [GeneratedRegex(@"\{.*?\}", RegexOptions.Singleline)]
    private static partial Regex JsonTokenRegex();

    public RcloneService(IRcloneProcessRunner processRunner, IRcloneConfigManager configManager)
    {
        _processRunner = processRunner;
        _configManager = configManager;
    }

    public Task<string> ExecuteCommandAsync(string[] arguments, IEnumerable<string>? inputLines = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        return _processRunner.ExecuteCommandAsync(arguments, inputLines, cancellationToken, timeout);
    }

    public async Task<List<RcloneItem>> ListItemsAsync(string path, bool dirsOnly = false, CancellationToken cancellationToken = default)
    {
        string[] args = ["lsjson", path, dirsOnly ? "--dirs-only" : "--files-only", "--drive-use-trash=false"];
        string json = await _processRunner.ExecuteCommandAsync(args, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize(json, RcloneJsonContext.Default.ListRcloneItem) ?? [];
        }
        catch (JsonException jex)
        {
            throw new FormatException($"Failed to parse Google Drive response for path {path}.", jex);
        }
    }

    public async Task<string> AuthorizeGoogleDrive(CancellationToken cancellationToken)
    {
        string output = await _processRunner.RunAuthorizationProcessAsync(cancellationToken);
        var match = JsonTokenRegex().Match(output);
        if (match.Success) return match.Value;
        throw new Exception("Failed to retrieve Google token.");
    }

    public Task CreateRemote(string name, string tokenJson, CancellationToken cancellationToken = default)
    {
        return _configManager.CreateRemoteAsync(name, tokenJson, cancellationToken);
    }

    public async Task<string> GetAccessTokenAsync(string rcloneRemote, CancellationToken cancellationToken = default)
    {
        await _processRunner.ExecuteCommandAsync(["about", $"{rcloneRemote}:"], null, cancellationToken);
        string configJson = await _processRunner.ExecuteCommandAsync(["config", "dump"], null, cancellationToken);
        using var configDoc = JsonDocument.Parse(configJson);

        if (!configDoc.RootElement.TryGetProperty(rcloneRemote, out var remoteNode) ||
            !remoteNode.TryGetProperty("token", out var tokenNode))
        {
            throw new InvalidOperationException($"Missing OAuth token inside rclone config for {rcloneRemote}.");
        }

        using var tokenDoc = JsonDocument.Parse(tokenNode.GetString() ?? "{}");
        if (!tokenDoc.RootElement.TryGetProperty("access_token", out var accessTokenNode))
        {
            throw new InvalidOperationException("Invalid token JSON format.");
        }

        return accessTokenNode.GetString()!;
    }

    public async Task DeleteRemoteAsync(string remoteName, CancellationToken cancellationToken = default) =>
        await _processRunner.ExecuteCommandAsync(["config", "delete", remoteName], null, cancellationToken);

    public async Task<string> ReadFileContentAsync(string rcloneRemote, string folderId, string fileName, CancellationToken cancellationToken = default) =>
        await _processRunner.ExecuteCommandAsync(["cat", $"{rcloneRemote},root_folder_id={folderId}:{fileName}"], null, cancellationToken);

    public async Task<List<string>> GetConfiguredRemotesAsync(CancellationToken cancellationToken = default)
    {
        // Removed error masking (silent catching). If Rclone fails (e.g., Timeout, I/O access denied), 
        // the exception will propagate. Returning an empty list was causing false integrity alerts.
        string output = await _processRunner.ExecuteCommandAsync(["listremotes"], null, cancellationToken);
        
        return output.Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.TrimEnd(':')).ToList();
    }
}