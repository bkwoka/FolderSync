using System.Threading;
using System.Threading.Tasks;

namespace FolderSync.Services.Interfaces;

public record UpdateInfo(string VersionName, string ReleaseUrl, bool IsUpdateAvailable);

public interface IUpdateService
{
    /// <summary>
    /// Checks for application updates from the remote repository.
    /// </summary>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>Update information if a check was successful; otherwise, null.</returns>
    Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
}