using System.Threading.Tasks;
using FolderSync.Models;

namespace FolderSync.Services.Interfaces;

/// <summary>
/// Defines the contract for application configuration management.
/// </summary>
// Removed IDisposable avoids ObjectDisposedException
// Avoids lifetime issues with SemaphoreSlim during background tasks or shutdown.
public interface IConfigService
{
    /// <summary>
    /// Loads the configuration from the persistent storage or returns default seed data if not found.
    /// </summary>
    Task<AppConfig> LoadConfigAsync();

    /// <summary>
    /// Saves the provided configuration to persistent storage.
    /// </summary>
    Task SaveConfigAsync(AppConfig config);
}