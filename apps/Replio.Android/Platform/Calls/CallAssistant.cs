using Android.Content;
using Android.OS;
using Android.Widget;
using ReplioDesktop.Models;
using ReplioDesktop.Services;
using ReplioDesktop.ViewModels;

namespace Replio.Android.Platform.Calls;

/// <summary>
/// Replio during calls: watches for a call, shows the button over it and runs the meeting from the menu through the
/// same view model as the app screen. One per process: it outlives the app screen, which Android may close while
/// the background service keeps the process alive. Everything here runs on the main thread (Avalonia's UI thread
/// on Android is the main looper).
/// </summary>
public sealed class CallAssistant
{
    // A question from the other side gets an automatic hint at most this often.
    private static readonly TimeSpan AutoHintPause = TimeSpan.FromSeconds(25);

    // A hint that has not come by then is reported on the card with «Повторить».
    private const int HintTimeoutMs = 60000;

    private readonly Handler _main = new(Looper.MainLooper!);
    private readonly OverlayController _overlay;
    private readonly Queue<string> _queued = new();
    private readonly List<Action> _afterWake = [];
    private CallWatcher? _watcher;
    private MainViewModel? _viewModel;
    private CaptureOrchestratorService? _capture;
    private RecordingMonitor? _monitor;
    private bool _startedDuringCall;
    private bool _startedFromBubble;
    private bool _bubbleHidden;
    private string _recordingSessionId = string.Empty;

    // The hint the card waits for: asked by the user, or automatic (a question was heard).
    private bool _hintRequested;
    private bool _autoRequested;
    private int _hintTicket;
    private HintRequest? _lastRequest;
    private HintRequest? _waitingFirst;
    private DateTime _failedAt = DateTime.MinValue;
    private DateTime _lastAutoHint = DateTime.MinValue;
#if REPLIO_DEMO
    private bool _demoRecording;
#endif

    private CallAssistant()
    {
        _overlay = new OverlayController(this);
        AudioSourceStatus.Changed += () => OnMain(() =>
        {
            _overlay.Refresh();
            StateChanged?.Invoke();
        });
    }

    /// <summary>What a hint asks the view model: a prompt (null for none), the command and the card's title.</summary>
    private sealed record HintRequest(string? Prompt, Func<MainViewModel, RelayCommand> Command, string? TitleKey);

    public static CallAssistant Instance { get; } = new();

    public bool IsRecording { get; private set; }

    public DateTime RecordingStarted { get; private set; }

    public bool InCall => _watcher?.InCall == true;

    /// <summary>The user dropped the button on «×»: it stays hidden until this call ends (the notification brings it back).</summary>
    public bool BubbleHidden => _bubbleHidden;

    /// <summary>On: a question heard in the call gets a «Что сказать» hint without asking.</summary>
    public bool AutoHints
    {
        get => Preferences.GetBoolean("auto_hints", true);
        set => Preferences.Edit()?.PutBoolean("auto_hints", value)?.Apply();
    }

    /// <summary>
    /// «Replio на звонках» in the calls setup: off, there is no button on calls, no background service and no
    /// «Ждёт звонка» notification (a recording started in the app still runs as usual).
    /// </summary>
    public bool WatchCalls
    {
        get => Preferences.GetBoolean("watch_calls", true);
        set
        {
            Preferences.Edit()?.PutBoolean("watch_calls", value)?.Apply();
            if (!value && !IsRecording)
            {
                _overlay.HideAll();
                ReplioService.StopIfIdle();
            }
            else if (value)
            {
                ReplioService.Start(global::Android.App.Application.Context);
                if (InCall && !_bubbleHidden)
                {
                    _overlay.ShowBubble(withTip: false);
                }
            }

            StateChanged?.Invoke();
        }
    }

    public event Action? StateChanged;

    /// <summary>Something outside changed (a permission, the accessibility switch): the app card refreshes.</summary>
    public void NotifyChanged() => StateChanged?.Invoke();

    public string ElapsedText
    {
        get
        {
            TimeSpan t = IsRecording ? DateTime.Now - RecordingStarted : TimeSpan.Zero;
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        }
    }

    public float Level => (float)Math.Clamp(_monitor?.Level ?? 0, 0, 1);

    /// <summary>What Replio hears right now, said plainly.</summary>
    public string HearingText
    {
        get
        {
            if (!IsRecording)
            {
                return CallText.Get("HintIdle");
            }

            string source = AudioSourceStatus.Describe();
            string hearing = Hearing(out _);
            return source.Length == 0 ? hearing : hearing.Length == 0 ? source : source + "\n" + hearing;
        }
    }

    /// <summary>One line for the recording screen: what Replio hears; problem when the user should do something.</summary>
    public string Hearing(out bool problem)
    {
        problem = false;
        if (!InCall)
        {
            problem = _monitor?.IsProblem == true;
            return _monitor?.StatusText ?? string.Empty;
        }

        if (AudioSourceStatus.Silenced || ReplioAccessibilityService.Instance == null)
        {
            problem = true;
            return CallText.Get("HearNothing");
        }

        if (_watcher?.IsSpeakerOn() == true)
        {
            return CallText.Get("HearBoth");
        }

        problem = true;
        return CallText.Get("HearMe");
    }

    /// <summary>
    /// The short line of the call menu and the button: «Слышу вас и собеседника» or «Слышу только вас: включите
    /// громкую связь» (<paramref name="problem"/>: shown in amber); <paramref name="speaker"/> picks the speaker icon.
    /// Before a call is recorded it still tells whether the speakerphone is on, since that decides what gets recorded.
    /// </summary>
    public string HearingLine(out bool problem, out bool speaker)
    {
        problem = false;
        speaker = false;
#if REPLIO_DEMO
        if (_demoRecording)
        {
            problem = true;
            return CallText.Get("HearMeShort");
        }
#endif
        if (!InCall)
        {
            if (!IsRecording)
            {
                return string.Empty;
            }

            problem = _monitor?.IsProblem == true;
            speaker = !problem;
            return _monitor?.StatusText ?? string.Empty;
        }

        if (AudioSourceStatus.Silenced || ReplioAccessibilityService.Instance == null)
        {
            problem = true;
            return CallText.Get("HearNothing");
        }

        if (_watcher?.IsSpeakerOn() == true)
        {
            speaker = true;
            return CallText.Get("HearBothShort");
        }

        problem = true;
        return CallText.Get("HearMeShort");
    }

    public string CurrentTopicName => _viewModel?.SelectedTopic?.Name is { Length: > 0 } name ? name : CallText.Get("MenuNoTopic");

    private static ISharedPreferences Preferences =>
        global::Android.App.Application.Context.GetSharedPreferences("replio_calls", FileCreationMode.Private)!;

    /// <summary>The app screen hands over its view model once it exists; what was asked before that runs now.</summary>
    public void Attach(MainViewModel viewModel, CaptureOrchestratorService capture, RecordingMonitor monitor)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        _viewModel = viewModel;
        _capture = capture;
        _monitor = monitor;
        viewModel.OverlayCaptureStateChanged += running => OnMain(() => OnRecordingChanged(running));
        viewModel.OverlaySuggestionChanged += text => OnMain(() => OnSuggestion(text));
        viewModel.OverlayAnswerFailed += () => OnMain(() =>
        {
            if (_hintRequested || _autoRequested)
            {
                FailHint(null);
            }
        });
        viewModel.NotificationRequested += (_, message, isError) => OnMain(() => OnViewModelNotice(message, isError));
        capture.TranscriptReceived += (_, line) => OnMain(() => OnTranscript(line));
        IsRecording = viewModel.IsCapturing;

        if (_afterWake.Count > 0)
        {
            // Replio was woken by a tap on the call (after a reboot the app screen did not exist yet): do what was
            // asked as soon as the screen has settled.
            Action[] actions = [.. _afterWake];
            _afterWake.Clear();
            _main.PostDelayed(() =>
            {
                foreach (Action action in actions)
                {
                    action();
                }
            }, 400);
        }
    }

    /// <summary>
    /// After a reboot only the background service runs, so the button works before the app screen exists: Replio's
    /// screen is started out of sight (it steps back to the call by itself) and <paramref name="then"/> runs once
    /// the view model is there.
    /// </summary>
    private void WakeApp(Action then)
    {
        _afterWake.Add(then);
        if (_afterWake.Count > 1)
        {
            return;
        }

        PhonePlatform.Log("[calls] The app screen does not exist yet: waking it.");
        if (!MainActivity.WakeInBackground())
        {
            _afterWake.Clear();
            Toast(CallText.Get("NotReady"));
            return;
        }

        Toast(CallText.Get("Waking"));
        _main.PostDelayed(() =>
        {
            if (_viewModel == null && _afterWake.Count > 0)
            {
                _afterWake.Clear();
                Toast(CallText.Get("NotReady"));
            }
        }, 15000);
    }

    /// <summary>Started by the background service.</summary>
    public void StartWatching(Context context)
    {
        if (_watcher != null)
        {
            return;
        }

        _watcher = new CallWatcher(context);
        _watcher.CallStateChanged += OnCallStateChanged;
        _watcher.Start();
    }

    public void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!IsRecording)
        {
            _overlay.HideAll();
        }
    }

    /// <summary>The accessibility service came or went: the button moves to the right owner.</summary>
    public void OnAccessibilityChanged(bool connected)
    {
        if (!connected)
        {
            _overlay.OnHostLost();
        }

        if ((InCall || IsRecording) && !_bubbleHidden)
        {
            _overlay.HideAll();
            _overlay.ShowBubble(withTip: false);
        }

        StateChanged?.Invoke();
    }

    private void OnCallStateChanged(bool inCall)
    {
        PhonePlatform.Log("[calls] Call " + (inCall ? "started" : "ended") + ".");
        _bubbleHidden = false;
        if (inCall)
        {
            if (WatchCalls)
            {
                _overlay.ShowBubble(withTip: !IsRecording);
            }
        }
        else
        {
            if (IsRecording && _startedDuringCall)
            {
                Stop();
                Toast(CallText.Get("CallEndedStopped"));
            }

            if (!IsRecording)
            {
                _overlay.HideAll();
            }

            // The phone's own recorder saves its file a few seconds after the call.
            _main.PostDelayed(ImportPhoneRecordings, 8000);
            _main.PostDelayed(ImportPhoneRecordings, 40000);
        }

        ReplioService.Refresh();
        StateChanged?.Invoke();
    }

    // ---------------------------------------------------------------- the button

    /// <summary>Dropped on «×» while dragged: no button until the call ends, unless the notification brings it back.</summary>
    public void HideBubbleForThisCall()
    {
        _bubbleHidden = true;
        _overlay.HideAll();
        Toast(CallText.Get("BubbleHidden"));
        ReplioService.Refresh();
    }

    /// <summary>«Показать кнопку» in the notification.</summary>
    public void ShowBubbleAgain()
    {
        _bubbleHidden = false;
        if (InCall || IsRecording)
        {
            _overlay.ShowBubble(withTip: false);
        }

        ReplioService.Refresh();
    }

    // ---------------------------------------------------------------- recording

    public void ToggleRecording()
    {
        if (IsRecording)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    /// <summary>The first tap on the button records the call right away; a note next to it offers to undo.</summary>
    public void StartFromBubble()
    {
        if (IsRecording)
        {
            return;
        }

        _startedFromBubble = true;
        Start();
    }

    /// <summary>The service first becomes a «microphone» service (Android lets an app record in the background only so), then records.</summary>
    public void Start()
    {
        if (_viewModel == null)
        {
            WakeApp(Start);
            return;
        }

        ReplioService.BeginRecording(global::Android.App.Application.Context);
    }

    /// <summary>Called by the service once it runs as a microphone service.</summary>
    internal void StartCaptureNow()
    {
        if (_viewModel == null || _viewModel.IsCapturing)
        {
            return;
        }

        _startedDuringCall = InCall;
        Execute(_viewModel.StartCaptureCommand);
    }

    public void Stop()
    {
        if (_viewModel?.IsCapturing == true)
        {
            Execute(_viewModel.StopCaptureCommand);
        }
    }

    private void OnRecordingChanged(bool running)
    {
        if (running == IsRecording)
        {
            return;
        }

        IsRecording = running;
        if (running)
        {
            RecordingStarted = DateTime.Now;
            _recordingSessionId = _viewModel?.PhoneRecordingSessionId ?? string.Empty;
            if ((InCall || _overlay.BubbleVisible) && !_bubbleHidden)
            {
                _overlay.ShowBubble(withTip: false);
                if (_startedFromBubble)
                {
                    // After the logo has turned into the timer: «Запись идёт · Отменить» under it for a few seconds.
                    _main.PostDelayed(() =>
                    {
                        if (IsRecording)
                        {
                            _overlay.ShowTip(CallText.Get("BubbleStarted"), 5000, Stop);
                        }
                    }, 350);
                }
            }
        }
        else
        {
            _startedDuringCall = false;
            _waitingFirst = null;
            _overlay.HideHint();
            if (!InCall)
            {
                _overlay.HideAll();
            }

            WatchForResults(_recordingSessionId);
            _recordingSessionId = string.Empty;
        }

        _startedFromBubble = false;
        _overlay.Refresh();
        ReplioService.Refresh();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// After a recording the summary takes a while: once it is ready and Replio is not on the screen, a notification
    /// «Итоги встречи готовы» opens it or shares it.
    /// </summary>
    private void WatchForResults(string sessionId)
    {
        if (sessionId.Length == 0)
        {
            return;
        }

        DateTime until = DateTime.Now.AddMinutes(20);

        void Check()
        {
            SessionSummary? session = _viewModel?.PhoneFindSession(sessionId);
            if (session == null)
            {
                return;
            }

            if (session.Status != SessionStatus.Completed)
            {
                if (DateTime.Now < until)
                {
                    _main.PostDelayed(Check, 4000);
                }

                return;
            }

            // The meeting gets its name from the summary a few seconds later.
            _main.PostDelayed(() =>
            {
                if (!MainActivity.IsInForeground && _viewModel?.PhoneFindSession(sessionId) is { } done)
                {
                    ReplioService.ShowResults(done.Id, done.Title, done.SummaryText);
                }
            }, 6000);
        }

        _main.PostDelayed(Check, 3000);
    }

    // ---------------------------------------------------------------- hints

    public void WhatToSay()
    {
        // A fresh question replaces the automatic answers still waiting behind the card.
        _queued.Clear();
        _overlay.SetQueued(0);
        RunHint(new HintRequest(null, vm => vm.WhatShouldISayCommand, "HintTitle"));
    }

    public void Ask(string question) => RunHint(new HintRequest(question, vm => vm.WhatShouldISayCommand, "HintTitle"));

    public void Another() => RunHint(new HintRequest(null, vm => vm.SuggestReplyCommand, null));

    public void Shorter() => RunHint(new HintRequest(CallText.Get("HintShorterPrompt"), vm => vm.WhatShouldISayCommand, null));

    public void Recap() => RunHint(new HintRequest(null, vm => vm.RecapCommand, "HintTitleRecap"));

    /// <summary>«Ещё 1» on the card: the next automatic answer that came while one was being read.</summary>
    public void ShowNextQueued()
    {
        if (_queued.Count == 0)
        {
            _overlay.SetQueued(0);
            return;
        }

        string next = _queued.Dequeue();
        _overlay.ShowHint(next, title: CallText.Get("HintTitleAuto"));
        _overlay.SetQueued(_queued.Count);
    }

    /// <summary>The card was closed: nothing waits for it any more.</summary>
    internal void OnHintClosed()
    {
        _hintRequested = false;
        _autoRequested = false;
        _waitingFirst = null;
        _queued.Clear();
        _hintTicket++;
    }

#if REPLIO_DEMO
    /// <summary>CI emulator only: the button, the menu or the hint card over the screen for a screenshot (overlay permission).</summary>
    public void DemoOverlay(string page)
    {
        _overlay.HideAll();
        _demoRecording = page is "callmenu" or "callhint";
        if (_viewModel?.IsCapturing != true)
        {
            IsRecording = _demoRecording;
            RecordingStarted = DateTime.Now.AddSeconds(-754);
        }

        switch (page)
        {
            case "callmenu":
                _overlay.ShowBubble(withTip: false);
                _overlay.ShowMenu();
                break;
            case "callhint":
                _overlay.ShowBubble(withTip: false);
                _overlay.ShowHint("Предложите зафиксировать срок 15 декабря и попросите прислать договор до пятницы.", title: CallText.Get("HintTitleAuto"));
                _queued.Clear();
                _queued.Enqueue("Уточните, кто подпишет договор с их стороны.");
                _overlay.SetQueued(_queued.Count);
                break;
            case "callstart":
                _overlay.ShowBubble(withTip: true);
                break;
            case "callmenuidle":
                _overlay.ShowBubble(withTip: false);
                _overlay.ShowMenu();
                break;
        }
    }
#endif

    /// <summary>
    /// Asks the view model for a hint and shows the card at the top of the screen with «Думаю…» until it comes. The
    /// hint is always about the call being recorded: before a recording the call is recorded first and the hint
    /// follows its first phrases (an old meeting open in the app is never used). An automatic hint that comes while
    /// an answer is being read waits behind it («Ещё 1»).
    /// </summary>
    private void RunHint(HintRequest request, bool automatic = false)
    {
        if (_viewModel == null)
        {
            if (!automatic)
            {
                WakeApp(() => RunHint(request));
            }

            return;
        }

        MainViewModel vm = _viewModel;
        if (!vm.IsCapturing)
        {
            if (automatic)
            {
                return;
            }

            if (InCall || _overlay.BubbleVisible)
            {
                WaitForFirstPhrases(request);
                Start();
            }
            else
            {
                Toast(CallText.Get("HintNotStarted"));
            }

            return;
        }

        vm.PhoneSelectRecordingSession();
        if (!vm.PhoneRecordingHasSpeech)
        {
            if (!automatic)
            {
                WaitForFirstPhrases(request);
            }

            return;
        }

        if (vm.IsAssistBusy)
        {
            if (!automatic)
            {
                // A second tap while one is on its way: the card nods and shows the answer when it comes.
                _hintRequested = true;
                _overlay.PulseHint();
            }

            return;
        }

        _waitingFirst = null;
        _lastRequest = request;
        vm.OverlayPrompt = request.Prompt ?? string.Empty;
        string? title = request.TitleKey == null ? null : CallText.Get(request.TitleKey);
        if (automatic)
        {
            _autoRequested = true;
            if (!_overlay.HintShowsAnswer)
            {
                _overlay.ShowHint(CallText.Get("HintThinking"), thinking: true, title: title);
            }
        }
        else
        {
            _hintRequested = true;
            _overlay.ShowHint(CallText.Get("HintThinking"), thinking: true, title: title);
        }

        int ticket = ++_hintTicket;
        _main.PostDelayed(() =>
        {
            if (ticket == _hintTicket && (_hintRequested || _autoRequested))
            {
                FailHint(CallText.Get("HintSlow"));
            }
        }, HintTimeoutMs);

        RelayCommand command = request.Command(vm);
        if (!command.CanExecute(null))
        {
            FailHint(null);
            return;
        }

        command.Execute(null);
    }

    /// <summary>Nothing said yet: the card says the hint comes after the first phrases, and it does.</summary>
    private void WaitForFirstPhrases(HintRequest request)
    {
        _waitingFirst = request;
        _overlay.ShowHint(CallText.Get("HintWaitFirst"), thinking: true, title: CallText.Get(request.TitleKey ?? "HintTitle"));
    }

    private void OnSuggestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Answers to the app's own screen show there; over a call only what was asked here or automatically.
        text = text.Trim();
        if (_hintRequested)
        {
            _hintRequested = false;
            _autoRequested = false;
            _hintTicket++;
            _overlay.ShowHint(text);
            return;
        }

        if (!_autoRequested)
        {
            return;
        }

        _autoRequested = false;
        _hintTicket++;
        if (_overlay.HintShowsAnswer)
        {
            _queued.Enqueue(text);
            _overlay.SetQueued(_queued.Count);
        }
        else
        {
            _overlay.ShowHint(text, title: CallText.Get("HintTitleAuto"));
        }
    }

    /// <summary>
    /// The hint did not come (no connection, not signed in, the limit): the card says so in plain words and offers
    /// «Повторить». An automatic hint behind an answer being read fails quietly.
    /// </summary>
    private void FailHint(string? reason)
    {
        bool asked = _hintRequested;
        bool waits = _autoRequested && _overlay.HintVisible && !_overlay.HintShowsAnswer;
        _hintRequested = false;
        _autoRequested = false;
        _hintTicket++;
        if (!asked && !waits)
        {
            return;
        }

        _failedAt = DateTime.Now;
        _overlay.ShowHintError(reason ?? FriendlyError.Describe(FriendlyError.Kind.Other), RetryAction());
    }

    private Action? RetryAction() => _lastRequest is { } last ? () => RunHint(last) : null;

    /// <summary>The view model's error while a hint is on its way (or just failed) explains the failure on the card.</summary>
    private void OnViewModelNotice(string message, bool isError)
    {
        if (!isError || _viewModel == null)
        {
            return;
        }

        bool waiting = _hintRequested || _autoRequested;
        bool justFailed = DateTime.Now - _failedAt < TimeSpan.FromSeconds(2) && _overlay.HintVisible;
        if (!waiting && !justFailed)
        {
            return;
        }

        string assistFailed = _viewModel.L["OpenAiRequestFailed"];
        string detail = assistFailed.Length > 0 && message.StartsWith(assistFailed, StringComparison.Ordinal) ? message[assistFailed.Length..] : message;
        FriendlyError.Kind kind = FriendlyError.Classify(detail);
        string friendly = kind == FriendlyError.Kind.None ? message.Trim() : FriendlyError.Describe(kind);
        if (waiting)
        {
            FailHint(friendly);
        }
        else
        {
            _overlay.ShowHintError(friendly, RetryAction());
        }
    }

    private void OnTranscript(TranscriptLine line)
    {
        if (!IsRecording || _viewModel == null)
        {
            return;
        }

        if (_viewModel.PhoneRecordingSessionId is { Length: > 0 } id)
        {
            _recordingSessionId = id;
        }

        string text = line.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        if (_waitingFirst is { } waiting)
        {
            // The view model adds the phrase to the meeting on its own turn: the hint follows right after.
            _waitingFirst = null;
            _main.PostDelayed(() =>
            {
                if (IsRecording)
                {
                    RunHint(waiting);
                }
            }, 600);
            return;
        }

        if (!AutoHints || !InCall || _bubbleHidden || _viewModel.IsAssistBusy)
        {
            return;
        }

        if (!text.EndsWith('?') || DateTime.Now - _lastAutoHint < AutoHintPause)
        {
            return;
        }

        _lastAutoHint = DateTime.Now;
        RunHint(new HintRequest(null, vm => vm.WhatShouldISayCommand, "HintTitleAuto"), automatic: true);
    }

    // ---------------------------------------------------------------- topic, snapshot, app

    public IEnumerable<(string Name, bool Selected, Action Select)> Topics()
    {
        if (_viewModel == null)
        {
            yield break;
        }

        MainViewModel vm = _viewModel;
        foreach (TopicItemViewModel item in vm.TopicItems.ToList())
        {
            if (item.Kind == TopicItemKind.All)
            {
                continue;
            }

            string name = item.Kind == TopicItemKind.Unsorted ? CallText.Get("MenuNoTopic") : item.Name;
            bool selected = ReferenceEquals(vm.SelectedTopicItem, item) ||
                            (item.Kind == TopicItemKind.Unsorted && vm.SelectedTopic == null);
            yield return (name, selected, () => vm.SelectedTopicItem = item);
        }
    }

    public void Snapshot()
    {
        if (_viewModel == null || !_viewModel.IsCapturing)
        {
            Toast(CallText.Get("HintNotStarted"));
            return;
        }

        // Let the menu slide away before the screen is taken.
        _main.PostDelayed(() =>
        {
            _viewModel.SnapshotStageChanged -= OnSnapshotStage;
            _viewModel.SnapshotStageChanged += OnSnapshotStage;
            Execute(_viewModel.CaptureSnapshotCommand);
        }, 450);
    }

    private void OnSnapshotStage(SnapshotStage stage, int number) => OnMain(() =>
    {
        if (stage == SnapshotStage.Saved)
        {
            // A camera-like flash over the screen, then the note.
            _overlay.Flash();
            Toast(CallText.Get("SnapshotDone"));
        }
        else if (stage == SnapshotStage.Failed)
        {
            Toast(CallText.Get("SnapshotFailed"));
        }
    });

    // ---------------------------------------------------------------- other sound sources

    /// <summary>Sound of other apps (video, broadcasts): asks Android each time, or turns it off.</summary>
    public void TogglePlayback()
    {
        if (PhoneAudioSource.HasProjection)
        {
            ReplioService.Instance?.DisableProjection();
            StateChanged?.Invoke();
            return;
        }

        if (!PhoneAudioSource.PlaybackSupported)
        {
            Toast(CallText.Get("PlaybackFailed"));
            return;
        }

        PlaybackConsentActivity.Launch(global::Android.App.Application.Context);
    }

    /// <summary>Recordings the phone made of the call itself go into the topic and get transcribed.</summary>
    public void ImportPhoneRecordings()
    {
        Context context = global::Android.App.Application.Context;
        if (_viewModel == null || !CallRecordingImporter.HasPermission(context))
        {
            return;
        }

        string folder = Path.Combine(MainViewModel.ImportsDirectory, "calls");
        Task.Run(() => CallRecordingImporter.CollectNew(context, folder)).ContinueWith(task =>
        {
            if (task.Result.Count > 0)
            {
                OnMain(() => Import(task.Result, "ImportedRecording"));
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Audio or video shared to Replio from another app.</summary>
    public void ImportShared(IReadOnlyList<global::Android.Net.Uri> uris)
    {
        Context context = global::Android.App.Application.Context;
        string folder = Path.Combine(MainViewModel.ImportsDirectory, "shared");
        Task.Run(() => CallRecordingImporter.CopyShared(context, uris, folder)).ContinueWith(task =>
        {
            if (task.Result.Count > 0)
            {
                OnMain(() => Import(task.Result, "ImportedShared"));
            }
        }, TaskScheduler.Default);
    }

    private void Import(IReadOnlyList<string> paths, string messageKey)
    {
        if (_viewModel == null)
        {
            return;
        }

        try
        {
            SessionTopic topic = _viewModel.ImportMediaFiles(paths, CallText.Get("ImportTopic"));
            Toast(CallText.Format(messageKey, topic.Name));
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[calls] Import failed: " + ex.Message);
        }
    }

    public void OpenApp()
    {
        Context context = global::Android.App.Application.Context;
        Intent? open = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? string.Empty);
        if (open == null)
        {
            return;
        }

        open.AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront);
        try
        {
            (ReplioAccessibilityService.Instance as Context ?? context).StartActivity(open);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[calls] App could not be opened: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void Execute(RelayCommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void OnMain(Action action)
    {
        if (Looper.MyLooper() == Looper.MainLooper)
        {
            action();
        }
        else
        {
            _main.Post(action);
        }
    }

    public static void Toast(string text)
    {
        try
        {
            global::Android.Widget.Toast.MakeText(global::Android.App.Application.Context, text, ToastLength.Long)?.Show();
        }
        catch (Exception)
        {
            // No toast from here.
        }
    }
}
