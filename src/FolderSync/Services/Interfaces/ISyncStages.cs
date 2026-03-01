using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Helpers;
using FolderSync.Models;

namespace FolderSync.Services.Interfaces;

public interface ISyncSanitizeStage
{
    Task RunAsync(RemoteInfo remote, IProgress<SyncProgressEvent> uiLogger, CancellationToken cancellationToken);
}

public interface ISyncConsolidateStage
{
    Task RunAsync(RemoteInfo remote, IProgress<SyncProgressEvent> uiLogger, CancellationToken cancellationToken);
}

public interface ISyncCrossAccountStage
{
    Task RunAsync(List<RemoteInfo> remotes, RemoteInfo master, IProgress<SyncProgressEvent> uiLogger,
        Action advanceProgress, CancellationToken cancellationToken);
}