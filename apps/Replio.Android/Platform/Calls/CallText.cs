namespace Replio.Android.Platform.Calls;

/// <summary>Texts of the call mode: the background notification, the Replio button, its menu and the setup card.</summary>
public static class CallText
{
    private static readonly Dictionary<string, (string Ru, string En)> Texts = new()
    {
        ["ChannelName"] = ("Replio в фоне", "Replio in the background"),
        ["WaitingTitle"] = ("Replio ждёт звонка", "Replio is waiting for a call"),
        ["WaitingText"] = ("Кнопка появится, когда начнётся разговор", "The button appears when a call starts"),
        ["InCallTitle"] = ("Идёт звонок", "Call in progress"),
        ["InCallText"] = ("Нажмите кнопку Replio, чтобы записать встречу", "Tap the Replio button to record the meeting"),
        ["RecordingTitle"] = ("Идёт запись {0}", "Recording {0}"),
        ["ActionRecord"] = ("Записать", "Record"),
        ["ActionStop"] = ("Стоп", "Stop"),
        ["ActionHint"] = ("Что сказать", "What to say"),
        ["BubbleTip"] = ("Звонок начался. Нажмите, чтобы записать встречу", "A call started. Tap to record the meeting"),
        ["MenuStart"] = ("Начать", "Start"),
        ["MenuTopic"] = ("Тема: {0}", "Topic: {0}"),
        ["MenuNoTopic"] = ("Без темы", "No topic"),
        ["MenuWhatToSay"] = ("Что сказать", "What to say"),
        ["MenuAutoHints"] = ("Подсказки сами", "Auto hints"),
        ["MenuSnapshot"] = ("Снимок экрана", "Screenshot"),
        ["MenuRecap"] = ("Коротко: о чём речь", "Quick recap"),
        ["MenuAsk"] = ("Спросить Replio о встрече…", "Ask Replio about the meeting…"),
        ["MenuOpenApp"] = ("Открыть Replio", "Open Replio"),
        ["HearBoth"] = ("Громкая связь: слышу обоих", "Speakerphone: I hear both sides"),
        ["HearMe"] = ("Слышу только вас. Включите громкую связь, чтобы Replio слышал собеседника", "I hear only you. Turn on the speakerphone so Replio hears the other side"),
        ["HearNothing"] = ("Android не даёт микрофон во время звонка. Включите «Слушать звонок» в Replio", "Android blocks the microphone during the call. Turn on «Listen to calls» in Replio"),
        ["HintIdle"] = ("Включите громкую связь, чтобы Replio слышал собеседника", "Turn on the speakerphone so Replio hears the other side"),
        ["HintNotStarted"] = ("Запись ещё не идёт", "Not recording yet"),
        ["HintThinking"] = ("Думаю…", "Thinking…"),
        ["HintTitle"] = ("ЧТО СКАЗАТЬ", "WHAT TO SAY"),
        ["HintAnother"] = ("Другой вариант", "Another option"),
        ["HintShorter"] = ("Короче", "Shorter"),
        ["HintShorterPrompt"] = ("Скажи то же самое короче, одной фразой.", "Say the same, shorter, in one sentence."),
        ["SnapshotDone"] = ("Снимок сохранён", "Screenshot saved"),
        ["SnapshotFailed"] = ("Снимок не получился", "Screenshot failed"),
        ["CallEndedStopped"] = ("Звонок закончился, запись остановлена. Итоги готовятся в Replio.", "The call ended, recording stopped. The summary is being prepared in Replio."),
        ["NotReady"] = ("Replio ещё запускается, попробуйте через секунду", "Replio is still starting, try again in a second"),
        ["SetupTitle"] = ("Replio на звонках", "Replio on calls"),
        ["SetupReady"] = ("Replio ждёт звонка. Когда начнётся звонок или встреча, у края экрана появится кнопка Replio. Приложение можно закрыть.", "Replio is waiting for a call. When a call or a meeting starts, the Replio button appears at the edge of the screen. You can close the app."),
        ["SetupNeeded"] = ("Чтобы Replio появлялся на звонках, включите:", "To have Replio show up on calls, turn on:"),
        ["SetupMic"] = ("Микрофон и уведомления", "Microphone and notifications"),
        ["SetupListen"] = ("Слушать звонок", "Listen to calls"),
        ["SetupListenHint"] = ("Android не даёт обычным приложениям микрофон во время звонка. Этот режим (служба специальных возможностей) разрешает Replio писать вместе со звонком и показывать кнопку поверх него. В списке найдите «Replio: слушать звонок».", "Android gives ordinary apps no microphone during a call. This mode (an accessibility service) lets Replio record alongside the call and show its button over it. Find «Replio: listen to calls» in the list."),
        ["SetupOverlay"] = ("Кнопка поверх других приложений", "Button over other apps"),
        ["SetupBattery"] = ("Не усыплять в фоне", "Keep running in the background"),
        ["SetupOn"] = ("Включено", "On"),
        ["SetupTurnOn"] = ("Включить", "Turn on"),
        ["SetupSpeaker"] = ("Собеседника слышно, когда включена громкая связь. Без неё Replio слышит только вас: так устроен Android.", "The other side is heard on speakerphone. Without it Replio hears only you: that's how Android works."),
        ["SetupPaused"] = ("Ожидание звонков выключено", "Waiting for calls is off"),
        ["SetupWatch"] = ("Ждать звонков", "Wait for calls"),
    };

    public static string Get(string key) =>
        Texts.TryGetValue(key, out (string Ru, string En) text)
            ? (string.Equals(MobileText.Language, "en", StringComparison.OrdinalIgnoreCase) ? text.En : text.Ru)
            : key;

    public static string Format(string key, params object[] args) => string.Format(Get(key), args);
}
