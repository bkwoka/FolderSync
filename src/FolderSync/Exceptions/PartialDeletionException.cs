using System;
using System.Collections.Generic;

namespace FolderSync.Exceptions;

/// <summary>
/// Domain exception thrown when a file deletion operation succeeds on some accounts
/// but fails on others (e.g., due to network errors).
/// This prevents silent error masking and allows the UI to inform the user about leftovers.
/// </summary>
public class PartialDeletionException : Exception
{
    /// <summary>
    /// Gets the list of friendly names of remotes where the deletion failed.
    /// </summary>
    public List<string> FailedRemotes { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PartialDeletionException"/> class.
    /// </summary>
    /// <param name="failedRemotes">The list of remotes that failed the deletion.</param>
    public PartialDeletionException(List<string> failedRemotes) 
        : base($"Failed to remove the file from the following accounts: {string.Join(", ", failedRemotes)}")
    {
        FailedRemotes = failedRemotes;
    }
}
