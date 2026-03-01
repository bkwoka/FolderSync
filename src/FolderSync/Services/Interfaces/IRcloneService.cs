using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;

namespace FolderSync.Services.Interfaces;

public interface IRcloneService
{
    Task<string> ExecuteCommandAsync(string[] arguments, IEnumerable<string>? inputLines = null,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null);

    Task<List<RcloneItem>> ListItemsAsync(string path, bool dirsOnly = false,
        CancellationToken cancellationToken = default);

    Task<string> AuthorizeGoogleDrive(CancellationToken cancellationToken);
    Task CreateRemote(string name, string tokenJson, CancellationToken cancellationToken = default);
    Task DeleteRemoteAsync(string remoteName, CancellationToken cancellationToken = default);

    Task<string> ReadFileContentAsync(string rcloneRemote, string folderId, string fileName,
        CancellationToken cancellationToken = default);

    Task<string> GetAccessTokenAsync(string rcloneRemote, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a list of all remotes currently configured in the Rclone environment.
    /// </summary>
    Task<List<string>> GetConfiguredRemotesAsync(CancellationToken cancellationToken = default);
}