using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;

namespace FolderSync.Services.Interfaces;

public interface IMeshPermissionService
{
    Task GrantMeshPermissionsAsync(RemoteInfo newRemote, List<RemoteInfo> existingRemotes,
        CancellationToken cancellationToken = default);

    Task RevokeMeshPermissionsAsync(RemoteInfo targetToRemove, List<RemoteInfo> existingRemotes,
        CancellationToken cancellationToken = default);
}