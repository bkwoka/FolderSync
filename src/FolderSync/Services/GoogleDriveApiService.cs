using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Services.Interfaces;
using NLog;
using System.Linq;

namespace FolderSync.Services;

/// <summary>
/// Provides high-level interaction with the Google Drive v3 REST API.
/// Handles folder verification, user metadata retrieval, and permission management.
/// </summary>
/// <param name="rcloneService">Service to retrieve scoped access tokens.</param>
/// <param name="httpClientFactory">Factory to create resilient HTTP clients.</param>
public class GoogleDriveApiService(IRcloneService rcloneService, IHttpClientFactory httpClientFactory)
    : IGoogleDriveApiService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public async Task<bool> VerifyFolderExistsAsync(string tokenJson, string folderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var tokenDoc = JsonDocument.Parse(tokenJson);
            if (!tokenDoc.RootElement.TryGetProperty("access_token", out var accessTokenProp) ||
                string.IsNullOrWhiteSpace(accessTokenProp.GetString()))
            {
                throw new FormatException("Invalid OAuth token: 'access_token' field is missing or empty.");
            }

            string accessToken = accessTokenProp.GetString()!;

            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response =
                await client.GetAsync($"https://www.googleapis.com/drive/v3/files/{folderId}?fields=id,mimeType",
                    cancellationToken);
            if (!response.IsSuccessStatusCode) return false;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("mimeType", out var mimeElement))
            {
                return mimeElement.GetString() == "application/vnd.google-apps.folder";
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Folder verification failed for ID: {0}", folderId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<(string Name, string Email)> GetGoogleUserInfoAsync(string tokenJson,
        CancellationToken cancellationToken = default)
    {
        Logger.Info("Fetching user info from Google Drive API...");

        using var tokenDoc = JsonDocument.Parse(tokenJson);
        if (!tokenDoc.RootElement.TryGetProperty("access_token", out var accessTokenProp) ||
            string.IsNullOrWhiteSpace(accessTokenProp.GetString()))
        {
            throw new FormatException("Invalid OAuth token: 'access_token' field is missing or empty.");
        }

        string accessToken = accessTokenProp.GetString()!;

        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response =
            await client.GetAsync("https://www.googleapis.com/drive/v3/about?fields=user", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Google API Error ({response.StatusCode}): {errorContent}");
        }

        string jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
        using var userDoc = JsonDocument.Parse(jsonResponse);

        if (!userDoc.RootElement.TryGetProperty("user", out var userElement))
        {
            throw new FormatException("Google API response missing 'user' object.");
        }

        string name = userElement.TryGetProperty("displayName", out var nameProp)
            ? (nameProp.GetString() ?? "Unknown User")
            : "Unknown User";

        if (!userElement.TryGetProperty("emailAddress", out var emailProp) ||
            string.IsNullOrWhiteSpace(emailProp.GetString()))
        {
            throw new InvalidOperationException("Google API response missing 'emailAddress'.");
        }

        string email = emailProp.GetString()!;

        return (name, email);
    }

    /// <inheritdoc />
    public async Task ShareFolderAsync(string rcloneRemote, string folderId, string targetEmail,
        CancellationToken cancellationToken = default)
    {
        Logger.Info("Attempting to grant permissions for {0} on folder {1} (Remote: {2})", targetEmail, folderId,
            rcloneRemote);
        string accessToken = await rcloneService.GetAccessTokenAsync(rcloneRemote, cancellationToken);

        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var payload = new { type = "user", role = "writer", emailAddress = targetEmail };
        string jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        // URL Encoding
        string safeFolderId = Uri.EscapeDataString(folderId);
        string apiUrl =
            $"https://www.googleapis.com/drive/v3/files/{safeFolderId}/permissions?sendNotificationEmail=false";

        var response = await client.PostAsync(apiUrl, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string errorMsg = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Google API error while granting permissions ({response.StatusCode}): {errorMsg}");
        }

        Logger.Info("Permissions granted successfully.");
    }

    /// <inheritdoc />
    public async Task RevokePermissionAsync(string rcloneRemote, string folderId, string targetEmail,
        CancellationToken cancellationToken = default)
    {
        Logger.Info("Attempting to revoke permissions for {0} from folder {1} (Remote: {2})", targetEmail, folderId,
            rcloneRemote);
        try
        {
            string accessToken = await rcloneService.GetAccessTokenAsync(rcloneRemote, cancellationToken);

            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // URL Encoding
            string safeFolderId = Uri.EscapeDataString(folderId);
            string getUrl =
                $"https://www.googleapis.com/drive/v3/files/{safeFolderId}/permissions?fields=permissions(id,emailAddress)";
            var getResponse = await client.GetAsync(getUrl, cancellationToken);

            if (!getResponse.IsSuccessStatusCode)
            {
                Logger.Warn("Could not retrieve permission list from Google API. Status: {0}", getResponse.StatusCode);
                return;
            }

            string jsonContent = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(jsonContent);

            if (doc.RootElement.TryGetProperty("permissions", out var permissionsArray))
            {
                string? targetPermissionId = null;

                foreach (var permission in permissionsArray.EnumerateArray())
                {
                    if (permission.TryGetProperty("emailAddress", out var emailElement) &&
                        emailElement.GetString()?.Equals(targetEmail, StringComparison.OrdinalIgnoreCase) == true &&
                        permission.TryGetProperty("id", out var idElement))
                    {
                        targetPermissionId = idElement.GetString();
                        break;
                    }
                }

                if (string.IsNullOrEmpty(targetPermissionId))
                {
                    Logger.Info(
                        "User {0} did not have explicit permissions (or was part of a group). Skipping revocation.",
                        targetEmail);
                    return;
                }

                string safePermId = Uri.EscapeDataString(targetPermissionId);
                string deleteUrl = $"https://www.googleapis.com/drive/v3/files/{safeFolderId}/permissions/{safePermId}";
                var deleteResponse = await client.DeleteAsync(deleteUrl, cancellationToken);

                if (deleteResponse.IsSuccessStatusCode)
                {
                    Logger.Info("Success! Permissions revoked for user {0}.", targetEmail);
                }
                else
                {
                    Logger.Warn("Sent DELETE request for permission, but received status: {0}",
                        deleteResponse.StatusCode);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "An error occurred while attempting to unbind account {0} on {1}. Proceeding with process.",
                targetEmail, rcloneRemote);
        }
    }

    /// <inheritdoc />
    public async Task TrashFileAsync(string rcloneRemote, string fileId, CancellationToken cancellationToken = default)
    {
        string accessToken = await rcloneService.GetAccessTokenAsync(rcloneRemote, cancellationToken);

        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var payload = new { trashed = true };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await client.PatchAsync($"https://www.googleapis.com/drive/v3/files/{fileId}", content,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new UnauthorizedAccessException("Cannot trash file - forbidden or not found.");
            }

            string error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Drive API Error ({response.StatusCode}): {error}");
        }
    }

    /// <inheritdoc />
    public async Task<string?> AutoDetectGoogleAiStudioFolderIdAsync(string tokenJson, CancellationToken cancellationToken = default)
    {
        try
        {
            using var tokenDoc = JsonDocument.Parse(tokenJson);
            if (!tokenDoc.RootElement.TryGetProperty("access_token", out var accessTokenProp) ||
                string.IsNullOrWhiteSpace(accessTokenProp.GetString()))
            {
                return null;
            }

            string accessToken = accessTokenProp.GetString()!;

            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Restrict the search to folders owned by the current user.
            // This prevents the application from incorrectly identifying folders shared by other accounts
            // (e.g., from an existing mesh) and ensures proper data isolation during initial setup.
            string query =
                Uri.EscapeDataString(
                    $"name='{AppConstants.TargetFolderName}' and mimeType='application/vnd.google-apps.folder' and trashed=false and 'me' in owners");
            
            string searchUrl =
                $"https://www.googleapis.com/drive/v3/files?q={query}&fields=files(id,createdTime)&orderBy=createdTime desc";

            var response = await client.GetAsync(searchUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("files", out var filesArray) || filesArray.GetArrayLength() == 0)
            {
                return null;
            }

            int count = filesArray.GetArrayLength();

            if (count == 1)
            {
                // Found exactly one OWNED folder - this is the most likely candidate.
                return filesArray[0].GetProperty("id").GetString();
            }

            // When multiple owned folders exist, we evaluate the most recent candidates
            // to identify the primary working directory based on the presence of application-specific data.
            Logger.Info("Found {0} owned folders named '{1}'. Inspecting up to 5 newest for data presence...", count,
                AppConstants.TargetFolderName);

            var topFolders = new System.Collections.Generic.List<string>();
            int limit = Math.Min(5, count);
            for (int i = 0; i < limit; i++)
            {
                var id = filesArray[i].GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(id)) topFolders.Add(id);
            }

            // Scatter-Gather pattern: concurrently scan detected folders for content depth.
            var inspectionTasks = topFolders.Select(async folderId =>
            {
                int fileCount = await CountPromptFilesInFolderAsync(client, folderId, cancellationToken);
                return (FolderId: folderId, Count: fileCount);
            });

            var results = await Task.WhenAll(inspectionTasks);

            // Select the folder with the highest number of conversation files.
            var bestFolder = results.OrderByDescending(r => r.Count).First();

            Logger.Info("Auto-detect selected owned folder {0} containing {1} prompt files.", bestFolder.FolderId,
                bestFolder.Count);
            return bestFolder.FolderId;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Automatic detection of the 'Google AI Studio' folder failed. Falling back to manual entry.");
            return null;
        }
    }

    /// <summary>
    /// Executes a server-side query to count .prompt files within a specific folder.
    /// </summary>
    private async Task<int> CountPromptFilesInFolderAsync(HttpClient client, string folderId,
        CancellationToken cancellationToken)
    {
        try
        {
            string query =
                Uri.EscapeDataString(
                    $"'{folderId}' in parents and mimeType='{AppConstants.AiStudioMimeType}' and trashed=false");
            string countUrl = $"https://www.googleapis.com/drive/v3/files?q={query}&fields=files(id)&pageSize=1000";

            var response = await client.GetAsync(countUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return 0;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("files", out var filesArray))
            {
                return filesArray.GetArrayLength();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "File counting failed for folder {0}.", folderId);
            return 0;
        }
    }
}