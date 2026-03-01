using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FolderSync.Views;

/// <summary>
/// Interaction logic for the SettingsView.axaml component.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}