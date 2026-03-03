using System;

namespace FolderSync.Exceptions;

/// <summary>
/// Domain exception thrown when the machine-bound key cannot be reconstructed 
/// (e.g., due to an OS reinstall or hardware change). Signals the need to restore from a backup.
/// </summary>
public class VaultKeyLostException : Exception
{
    public VaultKeyLostException(string message) : base(message)
    {
    }

    public VaultKeyLostException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
