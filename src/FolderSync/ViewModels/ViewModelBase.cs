using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FolderSync.Messages;

namespace FolderSync.ViewModels;

/// <summary>
/// Base class for all ViewModels. Provides global application lock awareness
/// via the <see cref="SyncStateChangedMessage"/> messenger pattern.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    /// <summary>
    /// Indicates whether the application is currently locked due to a background operation
    /// (e.g., synchronization, backup, or deletion). Bound in XAML to disable UI elements.
    /// </summary>
    [ObservableProperty] private bool _isAppLocked;

    protected ViewModelBase()
    {
        // Global listener: every ViewModel instance automatically learns
        // when any part of the application locks the system.
        WeakReferenceMessenger.Default.Register<SyncStateChangedMessage>(this, (r, m) =>
        {
            IsAppLocked = m.Value;
            OnAppLockChanged(m.Value);
        });
    }

    /// <summary>
    /// Virtual callback invoked when the global lock state changes.
    /// Override in derived ViewModels to refresh command CanExecute states.
    /// </summary>
    protected virtual void OnAppLockChanged(bool isLocked)
    {
    }
}