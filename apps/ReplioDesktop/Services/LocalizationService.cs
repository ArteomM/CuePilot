using System.Globalization;
using System.Resources;
using ReplioDesktop.ViewModels;

namespace ReplioDesktop.Services;

public class LocalizationService : ObservableObject
{
    private readonly ResourceManager _resourceManager = new("ReplioDesktop.Resources.Strings", typeof(LocalizationService).Assembly);
    private CultureInfo _currentCulture = CultureInfo.CurrentUICulture;

    private static readonly bool IsMac = OperatingSystem.IsMacOS();

    /// <summary>
    /// On macOS a string with a «_Mac» twin (KeyName_Mac) uses the twin: Mac key symbols (⌘V, ⌃⇧S) and Mac places
    /// instead of Ctrl and Windows settings. Every _Mac string exists in both languages.
    /// </summary>
    public string this[string key] =>
        (IsMac ? _resourceManager.GetString(key + "_Mac", _currentCulture) : null) ??
        _resourceManager.GetString(key, _currentCulture) ?? key;

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
