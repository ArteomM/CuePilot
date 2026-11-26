using Android.Content;
using Android.OS;
using Android.Widget;
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

    private readonly Handler _main = new(Looper.MainLooper!);
    private readonly OverlayController _overlay;
    private CallWatcher? _watcher;
    private MainViewModel? _viewModel;
    private CaptureOrchestratorService? _capture;
    private RecordingMonitor? _monitor;
    private bool _startedDuringCall;
    private bool _hintRequested;
    private DateTime _lastAutoHint = DateTime.MinValue;

    private CallAssistant()
    {
        _overlay = new OverlayController(this);
        AudioSourceStatus.Changed += () => OnMain(() =>
        {
            _overlay.Refresh();
            StateChanged?.Invoke();
        });
    }

    public static CallAssistant Instance { get; } = new();

    public bool IsRecording { get; private set; }

    public DateTime RecordingStarted { get; private set; }

    public bool InCall => _watcher?.InCall == true;

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
                if (InCall)
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

    public string CurrentTopicName => _viewModel?.SelectedTopic?.Name is { Length: > 0 } name ? name : CallText.Get("MenuNoTopic");

    private static ISharedPreferences Preferences =>
        global::Android.App.Application.Context.GetSharedPreferences("replio_calls", FileCreationMode.Private)!;

    /// <summary>The app screen hands over its view model once it exists.</summary>
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
            if (_hintRequested)
            {
                _hintRequested = false;
                _overlay.HideHint();
            }
        });
        capture.TranscriptReceived += (_, line) => OnMain(() => OnTranscript(line));
        IsRecording = viewModel.IsCapturing;
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

        if (InCall || IsRecording)
        {
            _overlay.HideAll();
            _overlay.ShowBubble(withTip: false);
        }

        StateChanged?.Invoke();
    }

    private void OnCallStateChanged(bool inCall)
    {
        PhonePlatform.Log("[calls] Call " + (inCall ? "started" : "ended") + ".");
        if (inCall)
        {
            if (WatchCalls)
            {
                _overlay.ShowBubble(withTip: true);
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

    /// <summary>The service first becomes a «microphone» service (Android lets an app record in the background only so), then records.</summary>
    public void Start()
    {
        if (_viewModel == null)
        {
            Toast(CallText.Get("NotReady"));
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
            if (InCall || _overlay.BubbleVisible)
            {
                _overlay.ShowBubble(withTip: false);
            }
        }
        else
        {
            _startedDuringCall = false;
            _overlay.HideHint();
            if (!InCall)
            {
                _overlay.HideAll();
            }
        }

        _overlay.Refresh();
        ReplioService.Refresh();
        StateChanged?.Invoke();
    }

    // ---------------------------------------------------------------- hints

    public void WhatToSay() => RunHint(null, vm => vm.WhatShouldISayCommand, "HintTitle");

    public void Ask(string question) => RunHint(question, vm => vm.WhatShouldISayCommand, "HintTitle");

    public void Another() => RunHint(null, vm => vm.SuggestReplyCommand, null);

    public void Shorter() => RunHint(CallText.Get("HintShorterPrompt"), vm => vm.WhatShouldISayCommand, null);

    public void Recap() => RunHint(null, vm => vm.RecapCommand, "HintTitleRecap");

#if REPLIO_DEMO
    /// <summary>CI emulator only: the menu or the hint card over the screen for a screenshot (overlay permission).</summary>
    public void DemoOverlay(string page)
    {
        _overlay.HideAll();
        if (page == "callmenu")
        {
            _overlay.ShowBubble(withTip: false);
            _overlay.ShowMenu();
        }
        else if (page == "callhint")
        {
            _overlay.ShowBubble(withTip: false);
            _overlay.ShowHint("Предложите зафиксировать срок 15 декабря и попросите прислать договор до пятницы.", title: CallText.Get("HintTitleAuto"));
        }
    }
#endif

    /// <summary>
    /// Asks the view model for a hint and shows the card at the top of the screen with «Думаю…» until it comes.
    /// <paramref name="titleKey"/> names the card («ЧТО СКАЗАТЬ», «О ЧЁМ РЕЧЬ»); null keeps the card's title.
    /// </summary>
    private void RunHint(string? prompt, Func<MainViewModel, RelayCommand> command, string? titleKey)
    {
        if (_viewModel == null)
        {
            Toast(CallText.Get("NotReady"));
            return;
        }

        if (!_viewModel.IsCapturing && _viewModel.SelectedSession == null)
        {
            Toast(CallText.Get("HintNotStarted"));
            return;
        }

        if (_viewModel.IsAssistBusy)
        {
            return;
        }

        _viewModel.OverlayPrompt = prompt ?? string.Empty;
        _hintRequested = true;
        _overlay.ShowHint(CallText.Get("HintThinking"), thinking: true, title: titleKey == null ? null : CallText.Get(titleKey));
        Execute(command(_viewModel));
    }

    private void OnSuggestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Answers to the app's own screen show there; over a call only what was asked here or automatically.
        if (_hintRequested || (InCall && IsRecording))
        {
            _hintRequested = false;
            _overlay.ShowHint(text.Trim());
        }
    }

    private void OnTranscript(TranscriptLine line)
    {
        if (!AutoHints || !IsRecording || !InCall || _viewModel == null || _viewModel.IsAssistBusy)
        {
            return;
        }

        string text = line.Text?.Trim() ?? string.Empty;
        if (!text.EndsWith('?') || DateTime.Now - _lastAutoHint < AutoHintPause)
        {
            return;
        }

        _lastAutoHint = DateTime.Now;
        RunHint(null, vm => vm.WhatShouldISayCommand, "HintTitleAuto");
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
            ReplioDesktop.Models.SessionTopic topic = _viewModel.ImportMediaFiles(paths, CallText.Get("ImportTopic"));
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
            context.StartActivity(open);
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
