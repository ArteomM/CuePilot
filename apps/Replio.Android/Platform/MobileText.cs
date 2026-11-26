namespace Replio.Android.Platform;

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
        ["RecordingNotificationTitle"] = ("Replio записывает разговор", "Replio is recording"),
        ["RecordingNotificationText"] = ("Слушает микрофон телефона. Остановить запись можно в Replio.", "Listening through the phone microphone. Stop the recording in Replio."),
        ["RecordingChannelName"] = ("Запись разговора", "Recording"),
        ["MicPermissionTitle"] = ("Нужен доступ к микрофону", "Microphone access needed"),
        ["MicPermissionDenied"] = ("Без доступа к микрофону Replio не сможет записать разговор. Разрешите его: Настройки Android → Приложения → Replio → Разрешения → Микрофон.", "Replio cannot record without the microphone. Allow it in Android Settings → Apps → Replio → Permissions → Microphone."),
        ["PhoneMicHint"] = ("Телефон записывает как диктофон: положите его рядом с собеседниками. Во время звонка на этом же телефоне Android отдаёт микрофон только приложению звонка, и Replio слышит тишину. Звонок с компьютера или другого телефона на громкой связи Replio запишет.", "The phone records like a voice recorder: put it near the people talking. During a call on this same phone Android gives the microphone only to the calling app, and Replio hears silence. A call on a computer or another phone on speaker is recorded."),
        ["Finishing"] = ("Распознаю последние фразы…", "Recognizing the last phrases…"),
        ["TabAssist"] = ("Подсказки", "Hints"),
        ["Stop"] = ("Остановить", "Stop"),
        ["Recording"] = ("Идёт запись", "Recording"),
        ["Share"] = ("Поделиться", "Share"),
        ["Close"] = ("Закрыть", "Close"),
        ["Topics"] = ("Группы", "Groups"),
        ["Back"] = ("Назад", "Back"),
        ["PhoneSttHint"] = ("Без галочки речь распознаётся на самом телефоне моделью whisper.cpp из списка ниже: интернет не нужен, но телефон греется и батарея садится быстрее. Через API распознаёт сервер провайдера: нужны ключ и интернет.", "Unchecked, speech is recognized on the phone itself by a whisper.cpp model from the list below: no internet needed, but the phone gets warm and the battery drains faster. Through the API the provider's server recognizes it: a key and internet are needed."),
        ["PhoneApiOnlyHint"] = ("На этом телефоне (32-битный процессор) речь распознаётся только через API: выберите провайдера, укажите ключ и модель распознавания.", "On this phone (a 32-bit processor) speech is recognized through the API only: choose the provider, the key and the speech model."),
        ["PhoneSpeechIntro"] = ("Модель скачивается один раз и хранится в памяти телефона; обновление Replio её не удаляет. Для распознавания через API модель не нужна.", "A model is downloaded once and kept in the phone's storage; updating Replio keeps it. Recognition through the API needs no model."),
        ["PhoneSpeechHardware"] = ("Ваш телефон: ядер процессора {0}, памяти {1} ГБ.", "Your phone: {0} CPU cores, {1} GB of memory."),
        ["PhoneSpeechDesc_small_q8_0"] = ("Хорошо распознаёт русский и почти не путает его с украинским. На современных телефонах успевает за живой речью; на слабых отстаёт, тогда для живой записи лучше base или API. Лучший выбор для большинства телефонов.", "Recognizes Russian well and rarely mixes it up with Ukrainian. Keeps up with live speech on current phones; on weak ones it lags, then base or the API is better for live recording. The best choice for most phones."),
        ["PhoneSpeechDesc_base_q8_0"] = ("Самая лёгкая и быстрая, меньше греет телефон, но для русского слабее: путает его с украинским и пропускает слова. Для слабых телефонов.", "The lightest and fastest, warms the phone less, but weaker for Russian: mixes it up with Ukrainian and skips words. For weak phones."),
        ["PhoneSpeechDesc_large_v3_turbo_q5_0"] = ("Самое высокое качество: лучше справляется с терминами, именами и быстрой речью. На телефоне медленная, для живой записи не подходит: берите её для записей в «Базе знаний» на мощных телефонах.", "The highest quality: better with terms, names and fast speech. Slow on a phone and not for live recording: use it for recordings in the knowledge base on powerful phones."),
        ["PhoneSpeechSuits"] = ("Подходит для вашего телефона", "Suits your phone"),
        ["PhoneSpeechMaySlow"] = ("Может работать медленно: нужно от {0} ядер и {1} ГБ памяти", "May be slow: needs {0}+ cores and {1} GB of memory"),
        ["PhoneSpeechNoSpace"] = ("Не хватает места в памяти телефона: нужно {0:0} МБ, свободно {1:0} МБ.", "Not enough storage on the phone: {0:0} MB needed, {1:0} MB free."),
        ["SettingsNavLook"] = ("Вид", "Look"),
        ["SettingsNavAbout"] = ("О приложении", "About"),
        ["SttOnPhone"] = ("На телефоне", "On the phone"),
        ["SttLocalPhoneTitle"] = ("Речь распознаётся на этом телефоне", "Speech is recognized on this phone"),
        ["SttLocalPhoneText"] = ("Запись никуда не отправляется. Модель распознавания скачивается ниже один раз.", "The recording is not sent anywhere. The recognition model is downloaded below once."),
        ["PhoneScreenContext"] = ("Снимок экрана по кнопке в мини-меню звонка", "Screenshot from the call mini-menu button"),
        ["PhoneScreenContextHint"] = ("Replio сохраняет снимок в сессию и учитывает его в подсказках. Нужен режим «Слушать звонок» (Android 11 и новее).", "Replio saves the screenshot to the session and uses it in hints. Needs «Listen to calls» (Android 11 and newer)."),
        ["WhatsNewShort"] = ("Что нового ↗", "What's new ↗"),
        ["SearchPlaceholder"] = ("Поиск по сессиям", "Search sessions"),
        ["PhoneWhatsNew"] = ("Replio 1.1.4 для Android:\n• Replio живёт в фоне: на звонке у края экрана появляется кнопка Replio. В её меню запись, «Что сказать», «О чём речь», снимок экрана и звук приложений; подсказка появляется вверху экрана.\n• «Подсказка на каждый вопрос»: собеседник спросил — Replio сразу показывает, что ответить.\n• Главная: группы сверху, крупные карточки встреч. Подержите палец на карточке, группе или подсказке — откроется меню действий.\n• Экран записи: большой таймер, одна строка статуса, подсказки крупными карточками и кнопки под большим пальцем. Ошибки связи не всплывают: Replio тихо повторяет и пишет понятный статус.\n• Плавные анимации нажатий, меню и переходов.\n• «Replio на звонках» включается и выключается в настройке звонков.\n• «Папка данных»: те же встречи и группы, что на компьютере, через папку, которую синхронизирует FolderSync.\n• Записи звонков самого телефона и файлы из «Поделиться» расшифровываются в Replio.", "Replio 1.1.4 for Android:\n• Replio lives in the background: on a call the Replio button appears at the screen edge. Its menu has recording, «What to say», «Recap», a screenshot and app sound; the hint appears at the top of the screen.\n• «A hint for every question»: when you are asked, Replio shows an answer right away.\n• Home: groups on top, big meeting cards. Hold a finger on a card, a group or a hint for its actions.\n• Recording screen: a big timer, one status line, hints as big cards and buttons under the thumb. Connection errors no longer pop up: Replio retries quietly and shows a plain status.\n• Smooth animations of taps, menus and pages.\n• «Replio on calls» can be turned on and off in the calls setup.\n• «Data folder»: the same meetings and groups as on the computer through a folder that FolderSync keeps in sync.\n• The phone's own call recordings and files shared to Replio are transcribed."),
        ["SourceTitle"] = ("Источник", "Source"),
        ["Move"] = ("Перенести", "Move"),
        ["ChooseTopic"] = ("Перенести в группу", "Move to group"),
        ["SessionActions"] = ("Сессия", "Session"),
        ["TopicEmptyHint"] = ("Здесь пока нет сессий. Нажмите «Записать» внизу экрана, чтобы записать разговор. Перенести сессию в группу можно кнопкой «⋯» на её карточке.", "No sessions here yet. Tap «Record» at the bottom to record a conversation. Move a session to a group with «⋯» on its card."),
        ["AskPlaceholder"] = ("Спросите о разговоре", "Ask about the conversation"),
        ["FolderCopyHint"] = ("Документы папки скопированы в Replio: их разобранный текст виден ниже. Чтобы добавить изменённые файлы, прикрепите папку заново.", "The folder's documents were copied into Replio: their parsed text is shown below. To add changed files, attach the folder again."),
        ["CannotOpenFile"] = ("Не удалось открыть файл: на телефоне нет приложения для этого типа файлов.", "Could not open the file: no app on the phone opens this type of file."),
        ["MonitorEngine"] = ("Распознаёт: {0}", "Recognition: {0}"),
        ["MonitorNoEngine"] = ("Распознавание речи не настроено: Настройки → Речь", "Speech recognition is not set up: Settings → Speech"),
        ["MonitorSilence"] = ("Микрофон не слышит звук", "The microphone hears nothing"),
        ["MonitorLagging"] = ("Текст немного запаздывает", "The text is a little behind"),
        ["MonitorRecognized"] = ("Слышу и записываю", "Hearing and writing it down"),
        ["MonitorNoSpeech"] = ("Слышу звук, но не слова. Говорите ближе к телефону", "I hear sound but no words. Speak closer to the phone"),
        ["MonitorHearing"] = ("Слышу речь…", "Hearing speech…"),
        ["MonitorListening"] = ("Слушаю…", "Listening…"),
        ["TestMicTitle"] = ("Проверка записи", "Recording check"),
        ["TestMicButton"] = ("Начать проверку (5 с)", "Start the check (5 s)"),
        ["TestMicHint"] = ("Нажмите и говорите 5 секунд: Replio запишет фразу, распознает её так же, как при записи разговора, и скажет, что работает, а что нет.", "Tap and speak for 5 seconds: Replio records the phrase, recognizes it as during a recording and says what works and what does not."),
        ["TestRecording"] = ("Говорите… 5 секунд", "Speak… 5 seconds"),
        ["TestRecognizing"] = ("Распознаю…", "Recognizing…"),
        ["TestBusy"] = ("Сейчас идёт запись: проверьте микрофон после её остановки.", "A recording is running: check the microphone after it stops."),
        ["TestMicFailed"] = ("Микрофон не открылся: {0}", "The microphone did not open: {0}"),
        ["TestMicSilent"] = ("Микрофон ничего не услышал. Проверьте разрешение на микрофон, запрет доступа к микрофону в шторке Android и не занят ли он звонком или другим приложением.", "The microphone heard nothing. Check the microphone permission, the microphone access switch in Android's quick settings and whether a call or another app holds it."),
        ["TestMicQuiet"] = ("Микрофон работает, но звук тихий (пик {0}%). Replio усилит его, но лучше говорить ближе к телефону.", "The microphone works but the sound is quiet ({0}% peak). Replio amplifies it, but speaking closer helps."),
        ["TestMicOk"] = ("Микрофон работает (пик громкости {0}%).", "The microphone works ({0}% peak)."),
        ["TestNoEngine"] = ("Распознавание не настроено: скачайте модель ниже или включите распознавание через API и укажите ключ.", "Recognition is not set up: download a model below or turn on recognition through the API with a key."),
        ["TestRecognized"] = ("{0} распознал за {1:0.0} с: «{2}»", "{0} recognized in {1:0.0} s: «{2}»"),
        ["TestNothingSaid"] = ("Речи не было слышно: {0} ничего не распознал ({1:0.0} с). Повторите и скажите фразу.", "No speech was heard: {0} recognized nothing ({1:0.0} s). Try again and say a phrase."),
        ["TestNotRecognized"] = ("{0} не разобрал слов ({1:0.0} с). Говорите громче или ближе к телефону.", "{0} recognized no words ({1:0.0} s). Speak louder or closer to the phone."),
        ["TestSlow"] = ("Телефон распознаёт медленнее, чем идёт речь (в первый раз сюда входит загрузка модели). Если повторная проверка так же медленна, для живой записи выберите модель base или распознавание через API.", "The phone recognizes slower than real time (the first time includes loading the model). If a second check is as slow, choose the base model or the API for live recording."),
        ["TestRecognitionFailed"] = ("{0}: ошибка распознавания: {1}", "{0}: recognition error: {1}"),
        ["RecordStart"] = ("Записать", "Record"),
        ["RecordOpen"] = ("Идёт запись", "Recording"),
        ["NavHome"] = ("Главная", "Home"),
        ["SessionTitle"] = ("Сессия", "Session"),
        ["MonitorLaggingBad"] = ("Телефон не успевает: текст сильно запаздывает", "The phone can't keep up: the text is far behind"),
        ["MonitorNetwork"] = ("Связь прерывается, часть текста может пропасть. Запись идёт", "The connection drops, some text may be lost. Still recording"),
        ["MonitorSignIn"] = ("Войдите в аккаунт, иначе текста не будет. Запись идёт", "Sign in, otherwise there will be no text. Still recording"),
        ["MonitorLimit"] = ("Лимит распознавания закончился, текста не будет. Запись идёт", "The recognition limit is used up, no text. Still recording"),
        ["MonitorTrouble"] = ("Распознавание сейчас не отвечает. Запись идёт", "Recognition is not answering right now. Still recording"),
        ["EmptyTitle"] = ("Здесь пока пусто", "Nothing here yet"),
        ["EmptyText"] = ("Нажмите «Записать», когда начнётся разговор. На звонках Replio появится сам.", "Tap «Record» when a conversation starts. On calls Replio shows up by itself."),
        ["EmptyGroupText"] = ("Запишите разговор или перенесите сюда сессию: нажмите на её карточку и подержите.", "Record a conversation or move a session here: press and hold its card."),
        ["NoResults"] = ("Ничего не нашлось", "Nothing found"),
        ["NewGroup"] = ("Новая группа", "New group"),
        ["GroupName"] = ("Название группы", "Group name"),
        ["SessionName"] = ("Название сессии", "Session name"),
        ["Save"] = ("Сохранить", "Save"),
        ["ShareSummary"] = ("Поделиться итогами", "Share the summary"),
        ["CallsWaiting"] = ("Ждёт звонка", "Waiting for calls"),
        ["CallsSetup"] = ("Настроить звонки", "Set up calls"),
        ["CallsPaused"] = ("Звонки выкл.", "Calls off"),
        ["PhoneDataHint"] = ("Чтобы на телефоне были те же встречи и группы, что на компьютере, храните их в папке на телефоне, которую приложение синхронизации (FolderSync, Autosync) держит равной облачной папке Replio с компьютера.", "To see the same meetings and groups as on the computer, keep them in a phone folder that a sync app (FolderSync, Autosync) keeps equal to the computer's Replio cloud folder."),
        ["PhoneDataNow"] = ("Сейчас данные здесь:", "The data is here now:"),
        ["PhoneDataDefault"] = ("В памяти Replio на этом телефоне", "In Replio's storage on this phone"),
        ["PhoneDataHow"] = ("Как связать с облаком", "How to link it to the cloud"),
        ["PhoneDataPick"] = ("Папка Replio на телефоне", "The Replio folder on the phone"),
        ["PhoneDataNeedAccess"] = ("Разрешите Replio «Доступ ко всем файлам» на открывшейся странице, вернитесь и нажмите «Выбрать папку…» ещё раз.", "Allow Replio «All files access» on the page that opened, come back and tap «Choose folder…» again."),
        ["PhoneDataNotLocal"] = ("Это папка облака, а не телефона. Выберите папку в памяти телефона, которую синхронизирует FolderSync.", "That is a cloud folder, not a phone folder. Choose a folder in the phone's storage that FolderSync syncs."),
        ["PhoneDataHowTitle"] = ("Те же данные, что на компьютере", "The same data as on the computer"),
        ["PhoneDataHowText"] = ("1. На компьютере: Настройки → Данные и обновления → «Папка данных» в Яндекс Диске или OneDrive, например «Яндекс Диск/Replio».\n2. На телефоне установите FolderSync (или Autosync) и войдите в тот же облачный аккаунт.\n3. В FolderSync создайте пару папок: облачная «Replio» ↔ папка «Replio» в памяти телефона, синхронизация в обе стороны, при изменениях и по расписанию.\n4. Здесь нажмите «Выбрать папку…», укажите эту папку «Replio» и перезапустите Replio.\n\nНастройки и вход остаются на каждом устройстве свои. Одну и ту же встречу лучше не менять на двух устройствах, пока они не синхронизировались.", "1. On the computer: Settings → Data and updates → «Data folder» in Яндекс Диск or OneDrive, for example «Yandex.Disk/Replio».\n2. On the phone install FolderSync (or Autosync) and sign in to the same cloud account.\n3. In FolderSync pair the cloud «Replio» folder with a «Replio» folder in the phone's storage, two-way, on changes and on a schedule.\n4. Here tap «Choose folder…», pick that «Replio» folder and restart Replio.\n\nSettings and sign-in stay per device. Better not to change one meeting on two devices before they sync."),
        ["BannerTitle"] = ("Настройте Replio для звонков", "Set Replio up for calls"),
        ["BannerText"] = ("Осталось шагов: {0}. Это займёт минуту", "{0} steps left, it takes a minute"),
        ["StopShort"] = ("Стоп", "Stop"),
        ["StopTitle"] = ("Остановить запись?", "Stop recording?"),
        ["StopText"] = ("Replio допишет последние фразы и подготовит итоги.", "Replio finishes the last phrases and prepares the summary."),
        ["StopContinue"] = ("Продолжить", "Keep going"),
        ["StopConfirm"] = ("Остановить", "Stop"),
        ["Stopping"] = ("Завершаю…", "Finishing…"),
        ["TabText"] = ("Текст", "Text"),
        ["ActWhatToSay"] = ("Что сказать", "What to say"),
        ["ActWhatToAsk"] = ("Что спросить", "What to ask"),
        ["ActRecap"] = ("О чём речь", "Recap"),
        ["ActAsk"] = ("Спросить", "Ask"),
        ["AskSend"] = ("Отправить", "Send"),
        ["HintsEmptyLive"] = ("Нажмите «Что сказать», и Replio подскажет ответ по ходу разговора.", "Tap «What to say» and Replio suggests an answer as the conversation goes."),
        ["HintsEmpty"] = ("Здесь будут подсказки Replio. Нажмите «Что сказать» или задайте вопрос о разговоре.", "Replio's hints appear here. Tap «What to say» or ask about the conversation."),
        ["Thinking"] = ("Думаю…", "Thinking…"),
        ["HintFailed"] = ("Подсказка не пришла. {0}", "The hint did not come. {0}"),
        ["HintBusy"] = ("Подсказка уже готовится", "A hint is already on its way"),
        ["ErrTitle"] = ("Не получилось", "Something went wrong"),
        ["ErrNetwork"] = ("Нет связи с сервером. Проверьте интернет и нажмите ещё раз.", "No connection to the server. Check the internet and tap again."),
        ["ErrSignIn"] = ("Войдите в аккаунт Replio: Настройки → Аккаунт.", "Sign in to Replio: Settings → Account."),
        ["ErrLimit"] = ("Лимит на сегодня закончился. Подробности: Настройки → Аккаунт.", "Today's limit is used up. Details: Settings → Account."),
        ["ErrOther"] = ("Что-то пошло не так. Попробуйте ещё раз.", "Something went wrong. Try again."),
        ["Copied"] = ("Скопировано", "Copied"),
        ["CopyLine"] = ("Копировать фразу", "Copy the phrase"),
        ["CopyAll"] = ("Копировать весь текст", "Copy all text"),
        ["Redo"] = ("Заново", "Again"),
        ["CopyingFolder"] = ("Копирую документы из папки…", "Copying the folder's documents…"),
    };

    /// <summary>Phone wording for shared texts that speak of dragging, Ctrl+V or the mouse (see LocalizationService).</summary>
    private static readonly Dictionary<string, (string Ru, string En)> SharedOverrides = new()
    {
        ["TopicNoSessions"] = ("В группе пока нет сессий. Запишите разговор кнопкой «Записать» или перенесите сессию в группу кнопкой «⋯» на её карточке.", "No sessions in this group yet. Record a conversation with «Record» or move a session here with «⋯» on its card."),
        ["KnowledgeListHint"] = ("Добавьте документы, аудио, видео, папку или свой текст кнопками выше. Replio разберёт их в Markdown (речь из аудио и видео станет текстом) и будет использовать в сводке, ответах и графе.", "Add documents, audio, video, a folder or your own text with the buttons above. Replio turns them into Markdown (speech in audio and video becomes text) and uses them in the summary, answers and graph."),
    };

    /// <summary>The phone's own text for a shared key, or null to use Strings.resx.</summary>
    public static string? Override(string key, string language) =>
        SharedOverrides.TryGetValue(key, out (string Ru, string En) text)
            ? (string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase) ? text.Ru : text.En)
            : null;

    public static string Get(string key) =>
        Texts.TryGetValue(key, out (string Ru, string En) text)
            ? (string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase) ? text.En : text.Ru)
            : key;
}
