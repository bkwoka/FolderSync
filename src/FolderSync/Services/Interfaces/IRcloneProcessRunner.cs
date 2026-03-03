using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderSync.Services.Interfaces;

public interface IRcloneProcessRunner
{
    Task<string> ExecuteCommandAsync(string[] arguments, IEnumerable<string>? inputLines = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null);
    Task<string> RunAuthorizationProcessAsync(CancellationToken cancellationToken);
}
