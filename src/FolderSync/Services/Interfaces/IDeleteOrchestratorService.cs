using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Models;

namespace FolderSync.Services.Interfaces;

public interface IDeleteOrchestratorService
{
    Task DeleteConversationAsync(string fileName, bool deleteAttachments, RemoteInfo masterRemote,
        List<RemoteInfo> allRemotes, CancellationToken cancellationToken = default);
}