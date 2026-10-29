namespace CuePilot.Android.Platform;

/// <summary>
/// The few texts only the phone shows (microphone permission, recording notification, the hints tab).
/// Everything else comes from the shared Strings.resx / Strings.ru.resx like on Windows and macOS.
/// </summary>
public sealed class MobileText : System.ComponentModel.INotifyPropertyChanged
{
    private static string _language = "ru";

    /// <summary>Bound from XAML as {Binding [Key], Source={x:Static platform:MobileText.Instance}}.</summary>
    public static MobileText Instance { get; } = new();

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>"ru" or "en", follows the app language (MainView sets it).</summary>
    public static string Language
    {
        get => _language;
        set
        {
            if (_language == value)
            {
                return;
            }

            _language = value;
            Instance.PropertyChanged?.Invoke(Instance, new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        }
    }

    public string this[string key] => Get(key);

    private static readonly Dictionary<string, (string Ru, string En)> Texts = new()
    {
        ["RecordingNotificationTitle"] = ("CuePilot записывает разговор", "CuePilot is recording"),
        ["RecordingNotificationText"] = ("Слушает микрофон телефона. Остановить запись можно в CuePilot.", "Listening through the phone microphone. Stop the recording in CuePilot."),
        ["RecordingChannelName"] = ("Запись разговора", "Recording"),
        ["MicPermissionTitle"] = ("Нужен доступ к микрофону", "Microphone access needed"),
        ["MicPermissionDenied"] = ("Без доступа к микрофону CuePilot не сможет записать разговор. Разрешите его: Настройки Android → Приложения → CuePilot → Разрешения → Микрофон.", "CuePilot cannot record without the microphone. Allow it in Android Settings → Apps → CuePilot → Permissions → Microphone."),
        ["PhoneMicHint"] = ("Запись идёт через микрофон телефона. Звук внутри других приложений Android записывать не даёт, поэтому для звонка включите громкую связь или положите телефон рядом с динамиком.", "Recording uses the phone microphone. Android does not let apps record sound inside other apps, so for a call turn on the speaker or put the phone next to it."),
        ["Finishing"] = ("Распознаю последние фразы…", "Recognizing the last phrases…"),
        ["TabAssist"] = ("Подсказки", "Hints"),
        ["Stop"] = ("Остановить", "Stop"),
        ["Recording"] = ("Идёт запись", "Recording"),
        ["Share"] = ("Поделиться", "Share"),
        ["Close"] = ("Закрыть", "Close"),
        ["Topics"] = ("Темы", "Topics"),
        ["Back"] = ("Назад", "Back"),
        ["PhoneSttHint"] = ("На телефоне речь распознаётся только через API: выберите провайдера, укажите ключ и модель распознавания.", "On the phone speech is recognized through the API only: choose the provider, the key and the speech model."),
        ["SourceTitle"] = ("Источник", "Source"),
        ["Move"] = ("Перенести", "Move"),
        ["ChooseTopic"] = ("Перенести в тему", "Move to topic"),
        ["SessionActions"] = ("Сессия", "Session"),
        ["TopicEmptyHint"] = ("Здесь пока нет сессий. Нажмите «Запустить CuePilot», чтобы записать разговор. Перенести сессию в тему можно кнопкой «⋯» на её карточке.", "No sessions here yet. Tap «Start CuePilot» to record a conversation. Move a session to a topic with «⋯» on its card."),
        ["AskPlaceholder"] = ("Спросите о разговоре", "Ask about the conversation"),
        ["CopyingFolder"] = ("Копирую документы из папки…", "Copying the folder's documents…"),
    };

    public static string Get(string key) =>
        Texts.TryGetValue(key, out (string Ru, string En) text)
            ? (string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase) ? text.En : text.Ru)
            : key;
}
