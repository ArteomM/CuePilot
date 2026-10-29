namespace CuePilot.Android.Platform;

/// <summary>
/// The texts only the phone shows (microphone permission, recording notification, phone speech models and hints).
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
        ["PhoneSttHint"] = ("Без галочки речь распознаётся на самом телефоне моделью whisper.cpp из списка ниже: интернет не нужен, но телефон греется и батарея садится быстрее. Через API распознаёт сервер провайдера: нужны ключ и интернет.", "Unchecked, speech is recognized on the phone itself by a whisper.cpp model from the list below: no internet needed, but the phone gets warm and the battery drains faster. Through the API the provider's server recognizes it: a key and internet are needed."),
        ["PhoneApiOnlyHint"] = ("На этом телефоне (32-битный процессор) речь распознаётся только через API: выберите провайдера, укажите ключ и модель распознавания.", "On this phone (a 32-bit processor) speech is recognized through the API only: choose the provider, the key and the speech model."),
        ["PhoneSpeechIntro"] = ("Модель скачивается один раз и хранится в памяти телефона; обновление CuePilot её не удаляет. Для распознавания через API модель не нужна.", "A model is downloaded once and kept in the phone's storage; updating CuePilot keeps it. Recognition through the API needs no model."),
        ["PhoneSpeechHardware"] = ("Ваш телефон: ядер процессора {0}, памяти {1} ГБ.", "Your phone: {0} CPU cores, {1} GB of memory."),
        ["PhoneSpeechDesc_small_q8_0"] = ("Хорошо распознаёт русский и почти не путает его с украинским. На современных телефонах успевает за живой речью; на слабых отстаёт, тогда для живой записи лучше base или API. Лучший выбор для большинства телефонов.", "Recognizes Russian well and rarely mixes it up with Ukrainian. Keeps up with live speech on current phones; on weak ones it lags, then base or the API is better for live recording. The best choice for most phones."),
        ["PhoneSpeechDesc_base_q8_0"] = ("Самая лёгкая и быстрая, меньше греет телефон, но для русского слабее: путает его с украинским и пропускает слова. Для слабых телефонов.", "The lightest and fastest, warms the phone less, but weaker for Russian: mixes it up with Ukrainian and skips words. For weak phones."),
        ["PhoneSpeechDesc_large_v3_turbo_q5_0"] = ("Самое высокое качество: лучше справляется с терминами, именами и быстрой речью. На телефоне медленная, для живой записи не подходит: берите её для записей в «Базе знаний» на мощных телефонах.", "The highest quality: better with terms, names and fast speech. Slow on a phone and not for live recording: use it for recordings in the knowledge base on powerful phones."),
        ["PhoneSpeechSuits"] = ("Подходит для вашего телефона", "Suits your phone"),
        ["PhoneSpeechMaySlow"] = ("Может работать медленно: нужно от {0} ядер и {1} ГБ памяти", "May be slow: needs {0}+ cores and {1} GB of memory"),
        ["PhoneSpeechNoSpace"] = ("Не хватает места в памяти телефона: нужно {0:0} МБ, свободно {1:0} МБ.", "Not enough storage on the phone: {0:0} MB needed, {1:0} MB free."),
        ["PhoneShowInScreenShare"] = ("Показывать CuePilot при записи экрана и трансляции", "Show CuePilot in screen recordings and casting"),
        ["PhoneShowInScreenShareHint"] = ("Если выключено, CuePilot не попадает в запись экрана, трансляцию экрана в созвоне и скриншоты: там вместо него чёрный экран. Применяется сразу; чтобы запомнить, нажмите «Сохранить настройки».", "When off, CuePilot is left out of screen recordings, screen casting in calls and screenshots: they show a black screen instead. Applies at once; to keep it, tap «Save settings»."),
        ["Modes"] = ("Режимы", "Modes"),
        ["WhatsNewShort"] = ("Что нового ↗", "What's new ↗"),
        ["SearchPlaceholder"] = ("Поиск по сессиям", "Search sessions"),
        ["PhoneWhatsNew"] = ("Версия 1.0.5 для Android, первая версия для телефона:\n• Запись разговора через микрофон телефона, подсказки «Что сказать» и «Что спросить», итоги и письмо по итогам.\n• Распознавание речи на самом телефоне (whisper.cpp, модели скачиваются в Настройках) или через API.\n• Темы, сводка, база знаний (документы, картинки, папки, аудио и видео, свой текст), вопросы по теме и граф, как на компьютере.\n• Режимы, поиск по сессиям, дневная и ночная тема, русский и английский.\n• CuePilot можно скрыть от записи экрана и трансляции.", "Version 1.0.5 for Android, the first phone version:\n• Recording through the phone microphone, «What to say» and «What to ask» hints, the summary and the follow-up email.\n• Speech recognition on the phone itself (whisper.cpp, models are downloaded in Settings) or through the API.\n• Topics, the topic summary, the knowledge base (documents, pictures, folders, audio and video, own text), topic questions and the graph, as on the computer.\n• Modes, session search, day and night theme, Russian and English.\n• CuePilot can be hidden from screen recording and casting."),
        ["SourceTitle"] = ("Источник", "Source"),
        ["Move"] = ("Перенести", "Move"),
        ["ChooseTopic"] = ("Перенести в тему", "Move to topic"),
        ["SessionActions"] = ("Сессия", "Session"),
        ["TopicEmptyHint"] = ("Здесь пока нет сессий. Нажмите «Запустить CuePilot», чтобы записать разговор. Перенести сессию в тему можно кнопкой «⋯» на её карточке.", "No sessions here yet. Tap «Start CuePilot» to record a conversation. Move a session to a topic with «⋯» on its card."),
        ["AskPlaceholder"] = ("Спросите о разговоре", "Ask about the conversation"),
        ["FolderCopyHint"] = ("Документы папки скопированы в CuePilot: их разобранный текст виден ниже. Чтобы добавить изменённые файлы, прикрепите папку заново.", "The folder's documents were copied into CuePilot: their parsed text is shown below. To add changed files, attach the folder again."),
        ["CannotOpenFile"] = ("Не удалось открыть файл: на телефоне нет приложения для этого типа файлов.", "Could not open the file: no app on the phone opens this type of file."),
        ["CopyingFolder"] = ("Копирую документы из папки…", "Copying the folder's documents…"),
    };

    public static string Get(string key) =>
        Texts.TryGetValue(key, out (string Ru, string En) text)
            ? (string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase) ? text.En : text.Ru)
            : key;
}
