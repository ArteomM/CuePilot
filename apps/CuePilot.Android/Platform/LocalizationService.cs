using System.Globalization;
using System.Resources;
using CuePilot.Android.Platform;
using CuePilotDesktop.ViewModels;

namespace CuePilotDesktop.Services;

/// <summary>
/// The phone copy of the shared LocalizationService: the same Strings.resx, except the few texts that tell a computer
/// user to drag, paste with Ctrl+V or right-click. For those the phone shows its own wording from
/// <see cref="MobileText.Override"/>, also where the shared ViewModels build a text from them.
/// </summary>
public class LocalizationService : ObservableObject
{
    private readonly ResourceManager _resourceManager = new("CuePilotDesktop.Resources.Strings", typeof(LocalizationService).Assembly);
    private CultureInfo _currentCulture = CultureInfo.CurrentUICulture;

    public string this[string key] =>
        MobileText.Override(key, _currentCulture.TwoLetterISOLanguageName) ?? _resourceManager.GetString(key, _currentCulture) ?? key;

    public string CurrentLanguageCode => _currentCulture.TwoLetterISOLanguageName;

    public void SetLanguage(string languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return;
        }

        CultureInfo culture = languageCode.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("ru-RU")
            : CultureInfo.GetCultureInfo("en-US");

        if (Equals(culture, _currentCulture))
        {
            return;
        }

        _currentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;

        OnPropertyChanged(nameof(CurrentLanguageCode));
        OnPropertyChanged("Item[]");
    }
}
