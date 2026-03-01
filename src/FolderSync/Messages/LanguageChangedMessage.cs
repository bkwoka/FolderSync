using CommunityToolkit.Mvvm.Messaging.Messages;

namespace FolderSync.Messages;

/// <summary>
/// Global message broadcast when the user changes the application language in settings.
/// </summary>
public class LanguageChangedMessage : ValueChangedMessage<string>
{
    public LanguageChangedMessage(string cultureCode) : base(cultureCode)
    {
    }
}
