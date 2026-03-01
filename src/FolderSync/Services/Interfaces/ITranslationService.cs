using System.Globalization;

namespace FolderSync.Services.Interfaces;

public interface ITranslationService
{
    string this[string key] { get; }
    CultureInfo Culture { get; set; }
}