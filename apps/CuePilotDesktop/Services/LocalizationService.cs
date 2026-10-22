using System.Globalization;
using System.Resources;
using CuePilotDesktop.ViewModels;

namespace CuePilotDesktop.Services;

public class LocalizationService : ObservableObject
{
    private readonly ResourceManager _resourceManager = new("CuePilotDesktop.Resources.Strings", typeof(LocalizationService).Assembly);
    private CultureInfo _currentCulture = CultureInfo.CurrentUICulture;

    public string this[string key] => _resourceManager.GetString(key, _currentCulture) ?? key;

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
