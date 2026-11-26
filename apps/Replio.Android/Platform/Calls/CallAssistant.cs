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

    /// <summary>Off when the user chose «Пауза»: no button on calls (the service keeps running for the app).</summary>
    public bool WatchCalls
    {
        get => Preferences.GetBoolean("watch_calls", true);
        set
        {
            Preferences.Edit()?.PutBoolean("watch_calls", value)?.Apply();
            if (!value && !IsRecording)
            {
                _overlay.HideAll();
            }
            else if (value && InCall)
            {
                _overlay.ShowBubble(withTip: false);
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

            if (!InCall)
            {
                return _monitor?.StatusText ?? string.Empty;
            }

            if (ReplioAccessibilityService.Instance == null)
            {
                return CallText.Get("HearNothing");
            }

            return _watcher?.IsSpeakerOn() == true ? CallText.Get("HearBoth") : CallText.Get("HearMe");
        }
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

    public void WhatToSay() => RunHint(null, vm => vm.WhatShouldISayCommand);

    public void Ask(string question) => RunHint(question, vm => vm.WhatShouldISayCommand);

    public void Another() => RunHint(null, vm => vm.SuggestReplyCommand);

    public void Shorter() => RunHint(CallText.Get("HintShorterPrompt"), vm => vm.WhatShouldISayCommand);

    public void Recap() => RunHint(null, vm => vm.RecapCommand);

    private void RunHint(string? prompt, Func<MainViewModel, RelayCommand> command)
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
        _overlay.ShowHint(CallText.Get("HintThinking"), thinking: true);
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
        WhatToSay();
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

        // Let the menu disappear before the screen is taken.
        _main.PostDelayed(() =>
        {
            _viewModel.SnapshotStageChanged -= OnSnapshotStage;
            _viewModel.SnapshotStageChanged += OnSnapshotStage;
            Execute(_viewModel.CaptureSnapshotCommand);
        }, 350);
    }

    private void OnSnapshotStage(SnapshotStage stage, int number) => OnMain(() =>
    {
        if (stage == SnapshotStage.Saved)
        {
            Toast(CallText.Get("SnapshotDone"));
        }
        else if (stage == SnapshotStage.Failed)
        {
            Toast(CallText.Get("SnapshotFailed"));
        }
    });

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
