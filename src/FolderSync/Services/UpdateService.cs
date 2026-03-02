using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Services.Interfaces;
using NLog;

namespace FolderSync.Services;

/// <summary>
/// Provides functionality for checking application updates via GitHub API.
/// </summary>
/// <param name="httpClientFactory">Factory for creating resilient HTTP clients.</param>
public class UpdateService(IHttpClientFactory httpClientFactory) : IUpdateService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        // Enforce a hard 10-second timeout for the update check to prevent hanging threads
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "FolderSync-AutoUpdater");

            string url =
                $"https://api.github.com/repos/{AppConstants.GitHubOwner}/{AppConstants.GitHubRepo}/releases/latest";

            var response = await client.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn("Update check failed. Status: {StatusCode}", response.StatusCode);
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("tag_name", out var tagElement) &&
                root.TryGetProperty("html_url", out var urlElement))
            {
                string tagName = tagElement.GetString() ?? "";
                string htmlUrl = urlElement.GetString() ?? "";

                // Ignore pre-release versions (e.g., "v1.2.3-beta", "1.2.3-rc1")
                if (tagName.Contains('-'))
                {
                    Logger.Trace("Ignoring pre-release version: {TagName}", tagName);
                    return null;
                }

                // Enforce 'v' prefix requirement for standard version tags (e.g., skips "1.2.3")
                if (!tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Trace("Skipping non-standard update tag: {TagName}", tagName);
                    return null;
                }

                string cleanRemoteVersion = tagName[1..];

                // Retrieve current version from assembly metadata for comparison
                string currentVerStr = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
                
                if (Version.TryParse(cleanRemoteVersion, out Version? remoteVer) &&
                    Version.TryParse(currentVerStr, out Version? localVer))
                {
                    bool isNewer = remoteVer > localVer;
                    Logger.Info("Update check internal: Local {LocalVer}, Remote {RemoteVer}. IsNewer: {IsNewer}",
                        localVer, remoteVer, isNewer);
                    return new UpdateInfo(tagName, htmlUrl, isNewer);
                }
            }
            else
            {
                Logger.Warn("GitHub API response format changed. Missing expected JSON keys.");
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            Logger.Warn("Update check was cancelled or timed out after 10 seconds.");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Network error during update check.");
            return null;
        }
    }
}