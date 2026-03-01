using CommunityToolkit.Mvvm.Messaging.Messages;

namespace FolderSync.Messages;

/// <summary>
/// Global message broadcast to notify the system when a synchronization process starts or finishes.
/// Value is True if sync is running, False otherwise.
/// </summary>
public class SyncStateChangedMessage : ValueChangedMessage<bool>
{
    public SyncStateChangedMessage(bool isSyncing) : base(isSyncing)
    {
    }
}
