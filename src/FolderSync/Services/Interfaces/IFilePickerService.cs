using System.Threading.Tasks;

namespace FolderSync.Services.Interfaces;

public interface IFilePickerService
{
    Task<string?> SaveFileDialogAsync(string title, string suggestedFileName, string extension);
    Task<string?> OpenFileDialogAsync(string title, string[] extensions);
}