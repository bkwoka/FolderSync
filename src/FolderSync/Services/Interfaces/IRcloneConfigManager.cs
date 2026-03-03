using System.Threading;
using System.Threading.Tasks;

namespace FolderSync.Services.Interfaces;

public interface IRcloneConfigManager
{
    string GetConfigPath();
    Task CreateRemoteAsync(string name, string tokenJson, CancellationToken cancellationToken = default);
}
