using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderSync.Services.Interfaces;

public interface IRcloneBootstrapper
{
    bool IsInstalled();
    string GetExecutablePath();
    Task InstallAsync(Action<double> progressCallback, CancellationToken cancellationToken = default);
}