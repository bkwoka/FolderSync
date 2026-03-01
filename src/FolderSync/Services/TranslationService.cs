using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using FolderSync.Resources;
using FolderSync.Services.Interfaces;

namespace FolderSync.Services;

/// <summary>
/// Service providing runtime translation and localization capabilities.
/// Supports dynamic culture switching with PropertyChanged notification for UI updates.
/// </summary>
public class TranslationService : INotifyPropertyChanged, ITranslationService
{
    private static ITranslationService _instance = new TranslationService();

    /// <summary>
    /// Gets the singleton instance of the translation service.
    /// </summary>
    public static ITranslationService Instance => _instance;

    /// <summary>
    /// Sets the singleton instance. Used during dependency injection initialization and unit testing.
    /// </summary>
    /// <param name="instance">The translation service instance to use.</param>
    public static void SetInstance(ITranslationService instance)
    {
        _instance = instance;
    }

    private TranslationService()
    {
    }

    /// <inheritdoc />
    public string this[string key]
    {
        get
        {
            var translation = AppStrings.ResourceManager.GetString(key, Culture);
            return translation ?? $"[{key}]";
        }
    }

    private CultureInfo _culture = CultureInfo.CurrentUICulture;
    private readonly object _cultureLock = new();

    /// <inheritdoc />
    public CultureInfo Culture
    {
        get
        {
            lock (_cultureLock) return _culture;
        }
        set
        {
            lock (_cultureLock)
            {
                if (Equals(_culture, value)) return;
                _culture = value;
            }

            OnPropertyChanged();
            OnPropertyChanged("Item"); // Notify indexer change to update bindings
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}