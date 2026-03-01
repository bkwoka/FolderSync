using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FolderSync.Helpers;

/// <summary>
/// Represents a progress event emitted by the sync engine to update the user interface.
/// </summary>
public record SyncProgressEvent(Guid TaskId, string Message, bool IsFinished);

/// <summary>
/// Represents a single log entry in the synchronization task list, supporting live UI updates.
/// </summary>
public partial class LogEntry : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    
    /// <summary>
    /// Gets or sets a value indicating whether the task associated with this log entry is currently active (e.g., for showing a spinner).
    /// </summary>
    [ObservableProperty] private bool _isActive;
    
    public string Text { get; init; } = string.Empty;
    public string? IconPath { get; init; }
    public string IconColor { get; init; } = "#e4eaec";
    public double LeftMargin { get; init; }
    public bool HasIcon => !string.IsNullOrEmpty(IconPath);
    public Thickness Margin => new Thickness(LeftMargin, 2, 0, 2);
}

/// <summary>
/// An observable collection that provides bulk removal capabilities to improve UI performance during log rotation.
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void RemoveRange(int index, int count)
    {
        this.CheckReentrancy();
        if (this.Items is List<T> list)
        {
            list.RemoveRange(index, count);
            this.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
