using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;

namespace FolderSync.Services.Interfaces;

public interface IDriveOrchestratorService
{
    Task<(string Token, string Name, string Email)> AuthorizeNewDriveAsync(CancellationToken cancellationToken);
    Task<bool> VerifyFolderExistsAsync(string token, string folderId, CancellationToken cancellationToken = default);
    Task AddNewDriveAsync(string name, string email, string folderId, string token, bool overwrite);
    Task DeleteDriveAsync(RemoteInfo targetToRemove);
    Task SetAsMasterAsync(RemoteInfo newMaster);
    Task<(AppConfig Config, List<RemoteInfo> CorruptedRemotes)> GetDrivesStateAsync();
}