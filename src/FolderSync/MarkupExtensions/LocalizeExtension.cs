using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using FolderSync.Services;

namespace FolderSync.MarkupExtensions;

/// <summary>
/// Custom Avalonia markup extension for localization.
/// Usage in XAML: Text="{i18n:Localize Key_Name}"
/// </summary>
public class LocalizeExtension : MarkupExtension
{
    public string Key { get; }

    public LocalizeExtension(string key)
    {
        Key = key;
    }

    /// <summary>
    /// Provides a dynamic binding to the TranslationService singleton.
    /// Uses indexer-based property paths to resolve translation keys.
    /// </summary>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Mode = BindingMode.OneWay,
            Source = TranslationService.Instance
        };

        return binding;
    }
}
