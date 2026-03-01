using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Helpers;
using FolderSync.Models;

namespace FolderSync.Services.Interfaces;

public interface ISyncEngine
{
    Task RunFullSync(List<RemoteInfo> remotes, RemoteInfo master, IProgress<SyncProgressEvent> uiLogger,
        IProgress<double> progressUpdater, CancellationToken cancellationToken = default);
}