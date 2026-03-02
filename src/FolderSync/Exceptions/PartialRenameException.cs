using System;
using System.Collections.Generic;

namespace FolderSync.Exceptions;

/// <summary>
/// Domain exception thrown when a file rename operation succeeds on the Master drive
/// but fails on some secondary remotes (e.g., due to network errors).
/// This prevents silent error masking and alerts the user to potential duplicates after synchronization.
/// </summary>
public class PartialRenameException : Exception
{
    /// <summary>
    /// Gets the list of friendly names of remotes where the rename failed.
    /// </summary>
    public List<string> FailedRemotes { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PartialRenameException"/> class.
    /// </summary>
    /// <param name="failedRemotes">The list of remotes that failed the rename.</param>
    public PartialRenameException(List<string> failedRemotes) 
        : base($"Failed to rename the file on the following accounts: {string.Join(", ", failedRemotes)}")
    {
        FailedRemotes = failedRemotes;
    }
}
