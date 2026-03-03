using System.Threading;
using System.Threading.Tasks;

namespace FolderSync.Services.Interfaces;

public interface IRcloneConfigManager
{
    string GetConfigPath();
    
    /// <summary>
    /// Creates a new remote entry in the configuration file. The token is encrypted at-rest.
    /// </summary>
    Task CreateRemoteAsync(string name, string tokenJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the configuration file, decrypts any encrypted tokens in memory, 
    /// and returns the plaintext INI representation.
    /// </summary>
    Task<string> GetDecryptedConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up any orphaned temporary configuration files left behind by application crashes.
    /// </summary>
    void CleanupStaleTempConfigs();

    /// <summary>
    /// Scans the existing configuration file for legacy plaintext tokens and encrypts them in-place.
    /// </summary>
    Task MigratePlaintextTokensAsync();
}
