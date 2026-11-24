using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>A row of the «Язык речи» dropdown.</summary>
public sealed record SpeechLanguageOption(string Code, string Title)
{
    public override string ToString() => Title;
}

/// <summary>
/// «Язык речи» in Settings: automatic detection or a fixed language. With «Автоопределение» DesktopCapture detects
/// the language of each source (own microphone, the other side) and locks onto it after the first phrases.
/// </summary>
public partial class MainViewModel
{
    private static readonly string[] SpeechLanguageCodes = ["auto", "ru", "en", "uk", "kk", "be", "uz", "de", "fr", "es", "it", "tr", "zh"];

    private IReadOnlyList<SpeechLanguageOption> _speechLanguageOptions = [];

    public IReadOnlyList<SpeechLanguageOption> SpeechLanguageOptions
    {
        get
        {
            if (_speechLanguageOptions.Count == 0)
            {
                _speechLanguageOptions = SpeechLanguageCodes
                    .Select(code => new SpeechLanguageOption(code, code == "auto" ? L["SpeechLanguageAuto"] : SpeechLanguageTitle(code)))
                    .ToList();
            }

            return _speechLanguageOptions;
        }
    }

    public SpeechLanguageOption? SelectedSpeechLanguage
    {
        get
        {
            string code = NormalizeSpeechLanguage(_settings.CaptureLanguage);
            return SpeechLanguageOptions.FirstOrDefault(o => o.Code == code) ?? SpeechLanguageOptions[0];
        }
        set
        {
            if (value == null || value.Code == NormalizeSpeechLanguage(_settings.CaptureLanguage))
            {
                return;
            }

            // Saved at once, like the theme: the next recording or media file uses it.
            _settings.CaptureLanguage = value.Code;
            PersistSettingsQuietly();
            OnPropertyChanged();
            AppendLog("[settings] Speech language: " + value.Code + ".");
        }
    }

    public static string NormalizeSpeechLanguage(string? code)
    {
        string value = (code ?? string.Empty).Trim().ToLowerInvariant();
        return value.Length == 0 ? "auto" : value;
    }

    /// <summary>Called when the interface language changes: the dropdown titles are localized.</summary>
    private void OnSpeechLanguageTitlesChanged()
    {
        _speechLanguageOptions = [];
        OnPropertyChanged(nameof(SpeechLanguageOptions));
        OnPropertyChanged(nameof(SelectedSpeechLanguage));
    }

    /// <summary>«[stt-lang][mic] ru» from DesktopCapture: the language a source locked onto in auto mode.</summary>
    private void OnSpeechLanguageDetected(string line)
    {
        int close = line.IndexOf(']', "[stt-lang]".Length);
        if (close < 0)
        {
            return;
        }

        string source = line["[stt-lang][".Length..close];
        string code = line[(close + 1)..].Trim();
        string who = string.Equals(source, "mic", StringComparison.OrdinalIgnoreCase) ? _settings.MicParticipantName : _settings.SystemParticipantName;
        _detectedSpeechLanguages[who] = SpeechLanguageTitle(code);
        string detected = string.Join(", ", _detectedSpeechLanguages.Select(pair => pair.Key + " — " + pair.Value));
        SttStatusText = SttStatusText.Split(" · ")[0] + " · " + string.Format(L["SpeechLanguageDetected"], detected);
    }

    private readonly Dictionary<string, string> _detectedSpeechLanguages = new(StringComparer.OrdinalIgnoreCase);

    private static string SpeechLanguageTitle(string code) => code switch
    {
        "ru" => "Русский",
        "en" => "English",
        "uk" => "Українська",
        "kk" => "Қазақша",
        "be" => "Беларуская",
        "uz" => "Oʻzbekcha",
        "de" => "Deutsch",
        "fr" => "Français",
        "es" => "Español",
        "it" => "Italiano",
        "tr" => "Türkçe",
        "zh" => "中文",
        _ => code
    };
}
