using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderSync.Models;

namespace FolderSync.Helpers;

/// <summary>
/// Represents a progress event emitted by the sync engine to update the user interface.
/// </summary>
public record SyncProgressEvent(Guid TaskId, string Message, bool IsFinished, LogEntryType Type = LogEntryType.Normal, int IndentLevel = 0);

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
    public LogEntryType Type { get; init; } = LogEntryType.Normal;
    public int IndentLevel { get; init; } = 0;

    /// <summary>
    /// Calculates the dynamic left margin based on the semantic indentation level (24px per level).
    /// </summary>
    public Thickness Margin => new Thickness(IndentLevel * 24, 2, 0, 2);
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
