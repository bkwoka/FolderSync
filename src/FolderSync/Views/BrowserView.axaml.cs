using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FolderSync.ViewModels;

namespace FolderSync.Views;

/// <summary>
/// Interaction logic for the BrowserView.axaml component.
/// </summary>
public partial class BrowserView : UserControl
{
    public BrowserView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Triggered when the control is attached to the visual tree. 
    /// Ensures that the drive list and file view are refreshed when switching between tabs.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is BrowserViewModel vm)
        {
            vm.AutoRefreshCommand.Execute(null);
        }
    }
}