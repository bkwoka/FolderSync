using System.Threading;
using System.Threading.Tasks;

namespace FolderSync.Services.Interfaces;

public interface IRcloneConfigManager
{
    string GetConfigPath();
    
    Task CreateRemoteAsync(string name, string tokenJson, CancellationToken cancellationToken = default);

    Task<string> GetDecryptedConfigAsync(CancellationToken cancellationToken = default);

    void CleanupStaleTempConfigs();

    Task MigratePlaintextTokensAsync();

    /// <summary>
    /// Safely updates and encrypts a token for a specific remote. 
    /// Used to capture tokens refreshed automatically by the Rclone engine.
    /// </summary>
    Task UpdateTokenAsync(string remoteName, string newTokenJson, CancellationToken cancellationToken = default);
}
