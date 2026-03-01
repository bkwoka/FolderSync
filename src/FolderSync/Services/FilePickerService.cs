using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using FolderSync.Services.Interfaces;

namespace FolderSync.Services;

public class FilePickerService : IFilePickerService
{
    private IStorageProvider? GetStorageProvider()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.StorageProvider;
        }

        return null;
    }

    public async Task<string?> SaveFileDialogAsync(string title, string suggestedFileName, string extension)
    {
        var provider = GetStorageProvider();
        if (provider == null) return null;

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            FileTypeChoices = new[]
                { new FilePickerFileType("FolderSync Backup") { Patterns = new[] { $"*{extension}" } } }
        });

        return file?.Path.LocalPath;
    }

    public async Task<string?> OpenFileDialogAsync(string title, string[] extensions)
    {
        var provider = GetStorageProvider();
        if (provider == null) return null;

        var patterns = extensions.Select(e => $"*{e}").ToArray();

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("FolderSync Backup") { Patterns = patterns } }
        });

        return files.FirstOrDefault()?.Path.LocalPath;
    }
}