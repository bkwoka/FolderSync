using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FolderSync.ViewModels;

namespace FolderSync.Views;

public partial class SyncView : UserControl
{
    private SyncViewModel? _currentViewModel;

    public SyncView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is SyncViewModel vm)
        {
            _currentViewModel = vm;
            _currentViewModel.Logs.CollectionChanged += OnLogsChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_currentViewModel != null)
        {
            _currentViewModel.Logs.CollectionChanged -= OnLogsChanged;
            _currentViewModel = null;
        }
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Reset)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var scrollViewer = this.FindControl<ScrollViewer>("LogScroll");
                scrollViewer?.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
    }
}