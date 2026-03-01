using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;

namespace FolderSync.Services.Interfaces;

public interface IRenameOrchestratorService
{
    Task RenameConversationAsync(string oldFullName, string newFullName, RemoteInfo masterRemote,
        List<RemoteInfo> allRemotes, CancellationToken cancellationToken = default);
}