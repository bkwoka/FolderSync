using System.Collections.Generic;

namespace FolderSync;

/// <summary>
/// Centralized storage for application-wide constants.
/// </summary>
public static class AppConstants
{
    /// <summary>Directory and file name constants for application infrastructure.</summary>
    public const string AppDataFolderName = "FolderSync";

    public const string ConfigFileName = "appsettings.json";
    public const string RcloneConfigFileName = "rclone.conf";
    public const string RcloneBinFolderName = "bin";

    // Google AI Studio specific constants
    public const string TargetFolderName = "Google AI Studio";
    public const string AiStudioMimeType = "application/vnd.google-makersuite.prompt";
    public const string HistoryFileName = "applet_access_history.json";

    // Rclone versioning and validation
    public const string RcloneTargetVersion = "v1.73.1";

    // GitHub repository metadata
    public const string GitHubOwner = "bkwoka";
    public const string GitHubRepo = "FolderSync";

    // Security and Performance defaults
    public const int Pbkdf2Iterations = 600_000;
    public const int DownloadBufferSize = 81920;
    public const int MinPasswordLength = 10;
    public const int MaxFileNameLength = 80;

    /// <summary>
    /// SHA256 checksums for official Rclone v1.73.1 binaries to ensure supply chain security.
    /// </summary>
    public static readonly Dictionary<string, string> RcloneHashes = new()
    {
        { "windows-amd64", "b054ffdb21585366fee6f6c5df6988286a99d3ad6c8ea9e935c9494eb637f495" },
        { "linux-amd64", "e9bad0be2ed85128e0d977bf36c165dd474a705ea950d18e1005cef98119407b" },
        { "linux-arm64", "8d40785a789612301aa27e5c6eaf8b4c6e7b9af93b3993280f6aab6f42bc1955" },
        { "osx-amd64", "67afc47a59122ad5600590fc593fdadfb123723470eba7e523c6a9f044be2862" }
    };
}