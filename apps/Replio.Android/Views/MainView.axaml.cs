using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Replio.Android.Platform;
using Replio.Android.Platform.Calls;
using ReplioDesktop.Models;
using ReplioDesktop.Services;
using ReplioDesktop.ViewModels;

namespace Replio.Android.Views;

/// <summary>
/// The phone screen: the same view model as on Windows and macOS, laid out in one column.
/// Dialogs and action sheets are in MainView.Dialogs.cs; the hint dialog of a recording is the desktop overlay's.
/// </summary>
public partial class MainView : UserControl
{
    private const int MaxChatItems = 100;
    private readonly MainViewModel _viewModel = null!;
    private readonly CaptureOrchestratorService _capture = null!;
    private readonly RecordingMonitor _monitor = null!;
    private CancellationTokenSource? _micTest;
    private readonly ObservableCollection<PhoneChatItem> _chat = [];
    private bool _assistTab;
    private bool _stopping;

    // Design-time and XAML loader constructor.
    public MainView()
    {
        InitializeComponent();
    }

    public MainView(string dataRoot)
        : this()
    {
        var settingsService = new JsonSettingsService();
        AppSettings settings = settingsService.Load();

        var localizationService = new LocalizationService();
        localizationService.SetLanguage(settings.UiLanguage);
        MobileText.Language = localizationService.CurrentLanguageCode;

        bool localWhisper = PrepareLocalWhisper(dataRoot);
        var diagnostics = new DiagnosticsViewModel(new EnvironmentDiagnosticsService(dataRoot));
        _capture = new CaptureOrchestratorService();
        _monitor = new RecordingMonitor(_capture);
        _viewModel = new MainViewModel(dataRoot, _capture, new PhoneScreenCapture(), diagnostics, settingsService, localizationService);

        // «Снимок экрана» works through the «Слушать звонок» accessibility service (Android 11+); without whisper.cpp
        // (a 32-bit phone) speech goes through the API only.
        _viewModel.CaptureScreenContextEnabled = global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.R;
        if (!localWhisper)
        {
            _viewModel.UseApiStt = true;
            SpeechModelsSection.IsVisible = false;
            SttHintText.Text = MobileText.Get("PhoneApiOnlyHint");
            SttHintText.IsVisible = true;
        }

        // A phone has no meeting windows to hide from a screen share: Replio is always visible in recordings and screenshots.
        PhonePlatform.SetHiddenFromScreenCapture(false);
        UpdateHardwareText();
        SetUpGraph();

        _viewModel.PlatformPermissionCheckRequested += () => PhonePlatform.HasMicrophonePermission();
        _viewModel.ConfirmationRequested += OnConfirmationRequested;
        _viewModel.NotificationRequested += (title, message, isError) => OnUi(() => OnNotification(title, message, isError));
        _viewModel.NoteEditor = OnNoteEditorRequested;
        _viewModel.OverlaySuggestionChanged += text => OnUi(() => AppendChat(isUser: false, text));
        _viewModel.OverlayQuestionAsked += text => OnUi(() => AppendChat(isUser: true, text));
        _viewModel.OverlayAnswerFailed += () => OnUi(RemoveThinking);
        _viewModel.OverlayCaptureStateChanged += running => OnUi(() =>
        {
            if (running)
            {
                ResetChat();
                _assistTab = true;
                AskRow.IsVisible = false;
            }

            UpdateSessionPanels();
        });
        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.SelectedMainPage):
                    OnUi(UpdatePage);
                    break;
                case nameof(MainViewModel.SelectedSessionTab):
                    OnUi(() =>
                    {
                        if (!_viewModel.IsCapturing)
                        {
                            _assistTab = false;
                        }

                        UpdateSessionPanels();
                    });
                    break;
                case nameof(MainViewModel.IsCapturing):
                case nameof(MainViewModel.SelectedSession):
                    OnUi(UpdateSessionPanels);
                    break;
                case nameof(MainViewModel.IsSelectionMode):
                case nameof(MainViewModel.ShowSessionsPanel):
                    OnUi(UpdateRecordButton);
                    break;
                case nameof(MainViewModel.CurrentSessionTitle):
                    OnUi(UpdatePageTitle);
                    break;
                case nameof(MainViewModel.SelectedTopicItem):
                case nameof(MainViewModel.VisibleSessionsCount):
                case nameof(MainViewModel.SessionQuery):
                    OnUi(UpdateEmptyText);
                    break;
                case nameof(MainViewModel.SelectedUiLanguage):
                    OnUi(() =>
                    {
                        MobileText.Language = _viewModel.L.CurrentLanguageCode;
                        UpdatePage();
                        UpdateEmptyText();
                        UpdateCallCard();
                        UpdateHardwareText();
                        UpdateGraphTypeNames();
                    });
                    break;
                case nameof(MainViewModel.IsEditingTopicName) when _viewModel.IsEditingTopicName:
                    OnUi(ShowRenameTopicDialog);
                    break;
                case nameof(MainViewModel.IsEditingSessionTitle) when _viewModel.IsEditingSessionTitle:
                    OnUi(ShowRenameSessionDialog);
                    break;
            }
        };

        DataContext = _viewModel;
        CallAssistant.Instance.Attach(_viewModel, _capture, _monitor);
        SetUpCallCard();
        AssistList.ItemsSource = _chat;
        _chat.CollectionChanged += (_, _) => UpdateAssistEmpty();
        ResetChat();
        _ = new LongPress(this, OnLongPress);
        SetUpLive();

        AskTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Ask();
            }
        };

        SetUpReadyLog();
        UpdatePage();
        UpdateEmptyText();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.BackRequested -= TopLevel_OnBackRequested;
            topLevel.BackRequested += TopLevel_OnBackRequested;
        }
    }

    private void TopLevel_OnBackRequested(object? sender, RoutedEventArgs e) => e.Handled = HandleBack();

    /// <summary>Sessions, topics and settings live in the app's own storage; this folder stands in for the install folder.</summary>
    /// <summary>
    /// The view model counts local recognition as installed when «whisper-cli» sits in the app folder (the Mac
    /// layout). On the phone whisper.cpp is a library inside the APK, so a small marker file stands for it.
    /// </summary>
    private static bool PrepareLocalWhisper(string dataRoot)
    {
        string marker = PlatformPaths.DefaultWhisperBin(dataRoot);
        bool available = LocalWhisper.IsAvailable;
        PhonePlatform.Log(available
            ? "whisper.cpp: " + LocalWhisper.Description
            : "whisper.cpp is not available: " + LocalWhisper.LoadError);
        try
        {
            if (available && !File.Exists(marker))
            {
                File.WriteAllText(marker, "whisper.cpp is built into Replio for Android (libreplio_whisper).\n");
            }
            else if (!available && File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
        catch (IOException)
        {
        }

        return available;
    }

    public static string ResolveDataRoot()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Replio", "app");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>The Android back button: closes a sheet or a dialog, then goes back to Home. False on Home (leave the app).</summary>
    public bool HandleBack()
    {
        if (DialogLayer.IsVisible)
        {
            CloseDialog();
            return true;
        }

        if (KnowledgeSheet.IsVisible)
        {
            KnowledgeSheet.IsVisible = false;
            return true;
        }

        if (CallSetupSheet.IsVisible)
        {
            CallSetupSheet.IsVisible = false;
            return true;
        }

        if (_viewModel.SelectedMainPage != "Home")
        {
            Execute(_viewModel.GoHomeCommand);
            return true;
        }

        return false;
    }

    private static void Execute(System.Windows.Input.ICommand command, object? parameter = null)
    {
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private void UpdatePage()
    {
        string page = _viewModel.SelectedMainPage;
        // A session is a page inside Home: back arrow, no bottom bar (its own hint buttons are at the bottom).
        BackButton.IsVisible = page == "Session";
        HomeLogo.IsVisible = page == "Home";
        BottomNav.IsVisible = page != "Session";
        CallPill.IsVisible = page != "Session";
        SessionMenuButton.IsVisible = page == "Session";
        UpdatePageTitle();
        UpdateRecordButton();
        KnowledgeSheet.IsVisible = false;
        UpdateSessionPanels();
    }

    /// <summary>The session's name and start in the app bar; «Настройки» on that page; nothing next to the logo on Home.</summary>
    private void UpdatePageTitle()
    {
        string page = _viewModel.SelectedMainPage;
        PageTitleText.Text = page switch
        {
            "Session" => _viewModel.CurrentSessionTitle,
            "Settings" => _viewModel.L["SettingsTab"],
            _ => string.Empty
        };
        DateTime? started = _viewModel.SelectedSession?.StartedAt;
        PageSubtitleText.Text = started is { } at ? at.ToString("d MMMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture) : string.Empty;
        PageSubtitleText.IsVisible = page == "Session" && started != null && !IsLiveSession;
    }

    /// <summary>Empty list: what to do, in two short lines; «Ничего не нашлось» for a search.</summary>
    private void UpdateEmptyText()
    {
        bool searching = !string.IsNullOrWhiteSpace(_viewModel.SessionQuery);
        EmptyTitleText.Text = MobileText.Get(searching ? "NoResults" : "EmptyTitle");
        EmptyText.Text = searching ? string.Empty : MobileText.Get(_viewModel.IsTopicSelected ? "EmptyGroupText" : "EmptyText");
        EmptyText.IsVisible = !searching;
    }

    /// <summary>The record button floats over Home only, and not while sessions are being selected (their action bar is there).</summary>
    private void UpdateRecordButton() =>
        RecordFab.IsVisible = _viewModel.SelectedMainPage == "Home" && !(_viewModel.IsSelectionMode && _viewModel.ShowSessionsPanel);

    private void Back_OnClick(object? sender, RoutedEventArgs e) => HandleBack();

    private void GraphZoomIn_OnClick(object? sender, RoutedEventArgs e) => TopicGraphView.ZoomBy(1.3);

    private void GraphZoomOut_OnClick(object? sender, RoutedEventArgs e) => TopicGraphView.ZoomBy(1 / 1.3);

    private void GraphFit_OnClick(object? sender, RoutedEventArgs e) => TopicGraphView.FitToView();

    private void RecordFab_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsCapturing)
        {
            Execute(_viewModel.OpenActiveSessionCommand);
        }
        else
        {
            StartRecording_OnClick(sender, e);
        }
    }

    private void UpdateHardwareText() => PhoneHardwareText.Text = string.Format(
        MobileText.Get("PhoneSpeechHardware"),
        SpeechModelCatalog.CpuCores,
        Math.Round(SpeechModelCatalog.TotalRamGb));

    private void WhatsNew_OnClick(object? sender, RoutedEventArgs e) =>
        ShowMessage(_viewModel.WhatsNewTitle, MobileText.Get("PhoneWhatsNew"));

    private void DeleteSpeechModel_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is SpeechModelItemViewModel item)
        {
            RunConfirmable(() => Execute(_viewModel.DeleteSpeechModelCommand, item));
        }
    }

    /// <summary>The copied document or recording opens in the app Android picks for its type.</summary>
    private void OpenKnowledgeOriginal_OnClick(object? sender, RoutedEventArgs e)
    {
        string? path = _viewModel.SelectedKnowledgeItem?.Source.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (Directory.Exists(path))
        {
            ShowMessage(_viewModel.L["KnowledgeOpenOriginal"], MobileText.Get("FolderCopyHint"));
        }
        else if (!File.Exists(path) || !PhonePlatform.OpenFile(path))
        {
            ShowMessage(_viewModel.L["ErrorTitle"], MobileText.Get("CannotOpenFile"));
        }
    }

    // ===== Recording =====

    private async void StartRecording_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!await PhonePlatform.RequestMicrophonePermissionAsync())
        {
            ShowMessage(MobileText.Get("MicPermissionTitle"), MobileText.Get("MicPermissionDenied"));
            return;
        }

        Execute(_viewModel.StartCaptureCommand);
    }

    /// <summary>Settings: 5 s from the microphone, recognized like a recording; a second tap cancels.</summary>
    private async void MicTest_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_micTest != null)
        {
            _micTest.Cancel();
            return;
        }

        MicTestResult.IsVisible = true;
        if (_viewModel.IsCapturing)
        {
            MicTestResult.Text = MobileText.Get("TestBusy");
            return;
        }

        if (!await PhonePlatform.RequestMicrophonePermissionAsync())
        {
            MicTestResult.Text = MobileText.Get("MicPermissionDenied");
            return;
        }

        using var cts = new CancellationTokenSource();
        _micTest = cts;
        MicTestButton.Content = MobileText.Get("TestRecording");
        MicTestLevel.Value = 0;
        MicTestLevel.IsVisible = true;
        MicTestResult.Text = string.Empty;
        try
        {
            string result = await MicrophoneTest.RunAsync(
                _viewModel.BuildSpeechTestOptions(),
                level => Dispatcher.UIThread.Post(() => MicTestLevel.Value = level),
                () => Dispatcher.UIThread.Post(() =>
                {
                    MicTestButton.Content = MobileText.Get("TestRecognizing");
                    MicTestLevel.IsVisible = false;
                }),
                cts.Token);
            MicTestResult.Text = result;
            MicTestResult.IsVisible = result.Length > 0;
        }
        finally
        {
            _micTest = null;
            MicTestLevel.IsVisible = false;
            MicTestButton.Content = MobileText.Get("TestMicButton");
        }
    }

    /// <summary>«Стоп» asks first: a recording stopped by a stray tap cannot be continued.</summary>
    private void StopRecording_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        ShowDialog(MobileText.Get("StopTitle"), MobileText.Get("StopText"),
        [
            (MobileText.Get("StopContinue"), null, false),
            (MobileText.Get("StopConfirm"), () => StopRecording(), true)
        ]);
    }

    /// <summary>The last phrases are recognized before the session is closed and summarized.</summary>
    private async void StopRecording()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        StopButton.IsEnabled = false;
        StopButtonText.Text = MobileText.Get("Stopping");
        UpdateLive();
        try
        {
            if (_demoLive)
            {
                _demoLive = false;
                UpdateSessionPanels();
                return;
            }

            await _capture.StopAndDrainAsync(TimeSpan.FromSeconds(45));
            Execute(_viewModel.StopCaptureCommand);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[ui] Stop failed: " + ex);
        }
        finally
        {
            _stopping = false;
            StopButton.IsEnabled = true;
            StopButtonText.Text = MobileText.Get("StopShort");
        }
    }

    // ===== Session page =====

    private bool IsLiveSession => _demoLive || (_viewModel.IsCapturing && _viewModel.SelectedSession?.Status == SessionStatus.Active);

    private void SessionTab_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        if (tag == "Assist")
        {
            _assistTab = true;
            UpdateSessionPanels();
            return;
        }

        _assistTab = false;
        _viewModel.SelectedSessionTab = tag;
        UpdateSessionPanels();
    }

    /// <summary>
    /// While recording only «Подсказки» and «Текст», with the big timer and the hint buttons; a finished session
    /// has its four pages. The assist page shows a short hint instead of an empty list.
    /// </summary>
    private void UpdateSessionPanels()
    {
        bool live = IsLiveSession;
        string tab = _assistTab ? "Assist" : _viewModel.SelectedSessionTab;
        if (live && tab != "Transcript")
        {
            tab = "Assist";
        }
        else if (!live && tab is not ("Summary" or "Transcript" or "Email" or "Assist"))
        {
            tab = "Summary";
        }

        LiveCard.IsVisible = live;
        LiveTabs.IsVisible = live;
        SessionTabs.IsVisible = !live;
        AssistList.IsVisible = tab == "Assist" && _chat.Count > 0;
        AssistEmpty.IsVisible = tab == "Assist" && _chat.Count == 0;
        AssistEmptyText.Text = MobileText.Get(live ? "HintsEmptyLive" : "HintsEmpty");
        SummaryPanel.IsVisible = tab == "Summary";
        TranscriptPanel.IsVisible = tab == "Transcript";
        CopyTranscriptButton.IsVisible = !live;
        EmailPanel.IsVisible = tab == "Email";
        AskBar.IsVisible = tab == "Assist";
        TabAssist.IsChecked = tab == "Assist";
        TabSummary.IsChecked = tab == "Summary";
        TabTranscript.IsChecked = tab == "Transcript";
        TabEmail.IsChecked = tab == "Email";
        TabLiveAssist.IsChecked = tab == "Assist";
        TabLiveTranscript.IsChecked = tab == "Transcript";
        PageSubtitleText.IsVisible = _viewModel.SelectedMainPage == "Session" && _viewModel.SelectedSession != null && !live;
        UpdateLive();
        if (TranscriptPanel.IsVisible && _viewModel.SelectedSessionTranscript.Count > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel.SelectedSessionTranscript.Count > 0)
                {
                    TranscriptList.ScrollIntoView(_viewModel.SelectedSessionTranscript[^1]);
                }
            }, DispatcherPriority.Background);
        }
    }

    private void UpdateAssistEmpty()
    {
        bool assist = AssistList.IsVisible || AssistEmpty.IsVisible;
        AssistList.IsVisible = assist && _chat.Count > 0;
        AssistEmpty.IsVisible = assist && _chat.Count == 0;
    }

    // ===== Recording screen: timer, one status line, new transcript lines in view =====

    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _notice;
    private DateTime _noticeUntil;
    private bool _dotOn = true;

    private void SetUpLive()
    {
        _liveTimer.Tick += (_, _) => UpdateLive();
        _viewModel.SelectedSessionTranscript.CollectionChanged += OnTranscriptChanged;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedSessionTranscript))
            {
                OnUi(() =>
                {
                    _viewModel.SelectedSessionTranscript.CollectionChanged -= OnTranscriptChanged;
                    _viewModel.SelectedSessionTranscript.CollectionChanged += OnTranscriptChanged;
                });
            }
        };
    }

    private void OnTranscriptChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => FollowTranscript();

    /// <summary>During a recording the newest phrase stays in view.</summary>
    private void FollowTranscript()
    {
        if (!IsLiveSession || !TranscriptPanel.IsVisible)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel.SelectedSessionTranscript.Count > 0)
            {
                TranscriptList.ScrollIntoView(_viewModel.SelectedSessionTranscript[^1]);
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Timer, a blinking red dot and one line of what Replio hears; a passing notice (a hint failed) for a few seconds.</summary>
    private void UpdateLive()
    {
        bool live = IsLiveSession && _viewModel.SelectedMainPage == "Session";
        if (!live)
        {
            _liveTimer.Stop();
            return;
        }

        if (!_liveTimer.IsEnabled)
        {
            _liveTimer.Start();
        }

        CallAssistant assistant = CallAssistant.Instance;
        LiveTimerText.Text = _demoLive && !assistant.IsRecording ? "12:47" : assistant.ElapsedText;
        _dotOn = !_dotOn || _stopping;
        LiveDot.Opacity = _dotOn ? 1 : 0.25;

        string text;
        bool problem = false;
        if (_stopping)
        {
            text = MobileText.Get("Finishing");
        }
        else if (_notice != null && DateTime.Now < _noticeUntil)
        {
            text = _notice;
            problem = true;
        }
        else if (_demoLive && !assistant.IsRecording)
        {
            _notice = null;
            text = MobileText.Get("MonitorRecognized");
        }
        else
        {
            _notice = null;
            text = assistant.Hearing(out problem);
        }

        LiveStatusText.Text = text;
        LiveStatusText.Classes.Set("problem", problem);
    }

    /// <summary>A short line in place of the status for 8 s (never a dialog over a running call).</summary>
    private void ShowLiveNotice(string text)
    {
        _notice = text;
        _noticeUntil = DateTime.Now.AddSeconds(8);
        UpdateLive();
    }

    /// <summary>
    /// A message of the view model. While recording nothing pops up over the call: a failed hint becomes a quiet
    /// note in the hints list, anything else a passing line under the timer. Otherwise a dialog, with an exception
    /// text turned into plain words (the original goes to the log).
    /// </summary>
    private void OnNotification(string title, string message, bool isError)
    {
        PhonePlatform.Log($"[notice] {title}: {message}");
        string assistFailed = _viewModel.L["OpenAiRequestFailed"];
        bool assist = !string.IsNullOrEmpty(assistFailed) && message.StartsWith(assistFailed, StringComparison.Ordinal);
        string detail = assist ? message[assistFailed.Length..] : message;
        FriendlyError.Kind kind = FriendlyError.Classify(detail);
        if (assist && kind == FriendlyError.Kind.None)
        {
            kind = FriendlyError.Kind.Other;
        }

        string friendly = kind == FriendlyError.Kind.None ? message : FriendlyError.Describe(kind);
        bool onSession = _viewModel.SelectedMainPage == "Session";
        if (assist && (onSession || IsLiveSession))
        {
            RemoveThinking();
            _chat.Add(PhoneChatItem.Notice(string.Format(MobileText.Get("HintFailed"), friendly)));
            ScrollChatToEnd();
            return;
        }

        if (_viewModel.IsCapturing && isError)
        {
            if (IsLiveSession && onSession)
            {
                ShowLiveNotice(friendly);
            }

            return;
        }

        ShowMessage(kind == FriendlyError.Kind.None ? title : MobileText.Get("ErrTitle"), friendly);
    }

    private void QuickAction_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } button)
        {
            return;
        }

        if (_viewModel.IsAssistBusy)
        {
            _viewModel.ShowFeedback(MobileText.Get("HintBusy"));
            return;
        }

        AppendChat(isUser: true, button.Content?.ToString() ?? string.Empty);
        switch (tag)
        {
            case "Reply":
                Execute(_viewModel.SuggestReplyCommand);
                break;
            case "Next":
                Execute(_viewModel.SuggestNextQuestionCommand);
                break;
            case "Recap":
                Execute(_viewModel.RecapCommand);
                break;
        }
    }

    /// <summary>«Спросить» opens the question box above the buttons (and closes it on a second tap).</summary>
    private void AskToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        AskRow.IsVisible = !AskRow.IsVisible;
        if (AskRow.IsVisible)
        {
            Dispatcher.UIThread.Post(() => AskTextBox.Focus(), DispatcherPriority.Input);
        }
    }

    private void Ask_OnClick(object? sender, RoutedEventArgs e) => Ask();

    /// <summary>The view model echoes the question back through OverlayQuestionAsked, as for the desktop overlay.</summary>
    private void Ask()
    {
        string question = AskTextBox.Text?.Trim() ?? string.Empty;
        if (question.Length == 0)
        {
            return;
        }

        if (_viewModel.IsAssistBusy)
        {
            _viewModel.ShowFeedback(MobileText.Get("HintBusy"));
            return;
        }

        _viewModel.OverlayPrompt = question;
        AskTextBox.Text = string.Empty;
        AskRow.IsVisible = false;
        Execute(_viewModel.WhatShouldISayCommand);
    }

    private void ResetChat() => _chat.Clear();

    private void AppendChat(bool isUser, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            if (!isUser)
            {
                RemoveThinking();
            }

            return;
        }

        string normalized = message.Trim();
        RemoveThinking();
        PhoneChatItem? last = _chat.LastOrDefault();
        if (last != null && last.IsUser == isUser && last.Message == normalized)
        {
            return;
        }

        _chat.Add(isUser ? PhoneChatItem.Request(normalized) : PhoneChatItem.Answer(normalized));
        if (isUser)
        {
            _chat.Add(PhoneChatItem.Thinking());
        }

        while (_chat.Count > MaxChatItems)
        {
            _chat.RemoveAt(0);
        }

        ScrollChatToEnd();
    }

    private void ScrollChatToEnd()
    {
        if (_chat.Count == 0)
        {
            return;
        }

        PhoneChatItem newest = _chat[^1];
        Dispatcher.UIThread.Post(() => AssistList.ScrollIntoView(newest), DispatcherPriority.Background);
    }

    private void RemoveThinking()
    {
        for (int i = _chat.Count - 1; i >= 0; i--)
        {
            if (_chat[i].IsThinking)
            {
                _chat.RemoveAt(i);
            }
        }
    }

    private void ShareSummary_OnClick(object? sender, RoutedEventArgs e) =>
        PhonePlatform.ShareText(_viewModel.CurrentSessionTitle, _viewModel.CurrentSessionSummary);

    private void ShareEmail_OnClick(object? sender, RoutedEventArgs e) =>
        PhonePlatform.ShareText(_viewModel.CurrentSessionTitle, _viewModel.CurrentFollowUpEmail);

    private void SessionPageMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSession is not { } session)
        {
            return;
        }

        var actions = new List<(string, Action, bool)>
        {
            (_viewModel.L["TopicRename"], () => Execute(_viewModel.StartRenameSessionCommand), false)
        };
        if (session.Status == SessionStatus.Completed)
        {
            actions.Add((_viewModel.L["FollowUpEmail"], () =>
            {
                _assistTab = false;
                _viewModel.SelectedSessionTab = "Email";
                Execute(_viewModel.FollowUpEmailCommand);
                UpdateSessionPanels();
            }, false));
            actions.Add((MobileText.Get("ChooseTopic"), () => ShowMoveSheet(_viewModel.GetActionSessionIds(session), session.TopicId), false));
            actions.Add((_viewModel.L["MenuDeleteSession"], () => RunConfirmable(() => Execute(_viewModel.DeleteSessionCommand, session)), true));
        }

        ShowActions(session.Title, actions);
    }

    /// <summary>Renaming in a small dialog (the phone has no room for a title being edited in place).</summary>
    private void ShowRenameSessionDialog()
    {
        var box = new TextBox { Text = _viewModel.EditableSessionTitle, Margin = new Thickness(0, 12, 0, 0) };
        ShowDialog(MobileText.Get("SessionName"), string.Empty,
        [
            (_viewModel.L["DialogCancel"], () => _viewModel.CancelRenameSessionTitle(), false),
            (MobileText.Get("Save"), () =>
            {
                _viewModel.EditableSessionTitle = box.Text ?? string.Empty;
                Execute(_viewModel.SaveSessionTitleCommand);
            }, false)
        ],
        [box],
        onDismiss: () => _viewModel.CancelRenameSessionTitle());
        FocusAll(box);
    }

    private void ShowRenameTopicDialog()
    {
        string original = _viewModel.EditableTopicName;
        var box = new TextBox { Text = original, Margin = new Thickness(0, 12, 0, 0) };
        void Cancel()
        {
            _viewModel.EditableTopicName = original;
            _viewModel.CommitTopicRename();
        }

        ShowDialog(MobileText.Get("GroupName"), string.Empty,
        [
            (_viewModel.L["DialogCancel"], () => Cancel(), false),
            (MobileText.Get("Save"), () =>
            {
                _viewModel.EditableTopicName = box.Text ?? string.Empty;
                _viewModel.CommitTopicRename();
            }, false)
        ],
        [box],
        onDismiss: () => Cancel());
        FocusAll(box);
    }

    private static void FocusAll(TextBox box) => Dispatcher.UIThread.Post(() =>
    {
        box.Focus();
        box.SelectAll();
    }, DispatcherPriority.Input);

    // ===== Home: sessions and topics =====

    private void SessionCard_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionSummary session })
        {
            return;
        }

        if (_viewModel.IsSelectionMode)
        {
            session.IsChecked = !session.IsChecked;
            return;
        }

        Execute(_viewModel.OpenSessionCommand, session);
    }

    /// <summary>Press and hold on a card: everything that can be done with the session.</summary>
    private void ShowSessionActions(SessionSummary session)
    {
        var actions = new List<(string, Action, bool)>
        {
            (_viewModel.L["MenuOpenSession"], () => Execute(_viewModel.OpenSessionCommand, session), false),
            (MobileText.Get("ChooseTopic"), () => ShowMoveSheet([session.Id], session.TopicId), false)
        };
        if (!string.IsNullOrWhiteSpace(session.SummaryText))
        {
            actions.Add((MobileText.Get("ShareSummary"), () => PhonePlatform.ShareText(session.Title, session.SummaryText), false));
        }

        actions.Add((_viewModel.L["SelectSessions"], () =>
        {
            if (!_viewModel.IsSelectionMode)
            {
                Execute(_viewModel.ToggleSelectionModeCommand);
            }

            session.IsChecked = true;
        }, false));
        actions.Add((_viewModel.L["MenuDeleteSession"], () => RunConfirmable(() => Execute(_viewModel.DeleteSessionCommand, session)), true));
        ShowActions(session.Title, actions);
    }

    private void MoveChecked_OnClick(object? sender, RoutedEventArgs e)
    {
        List<string> ids = _viewModel.CompletedSessions.Where(s => s.IsChecked).Select(s => s.Id).ToList();
        if (ids.Count > 0)
        {
            ShowMoveSheet(ids, currentTopicId: null);
        }
    }

    private void DeleteChecked_OnClick(object? sender, RoutedEventArgs e) =>
        RunConfirmable(() => Execute(_viewModel.DeleteCheckedSessionsCommand));

    /// <summary>One row per topic, plus «Убрать из темы» and «В новую тему…», like the desktop card menu.</summary>
    private void ShowMoveSheet(IReadOnlyList<string> ids, string? currentTopicId)
    {
        var actions = new List<(string, Action, bool)>();
        foreach (TopicItemViewModel item in _viewModel.TopicItems.Where(i => i.IsTopic && i.Topic!.Id != currentTopicId))
        {
            actions.Add((string.Format(_viewModel.L["MenuMoveToTopic"], item.Name), () => _viewModel.MoveSessionsToTopic(ids, item), false));
        }

        if (currentTopicId != string.Empty)
        {
            TopicItemViewModel? unsorted = _viewModel.TopicItems.FirstOrDefault(i => i.Kind == TopicItemKind.Unsorted);
            actions.Add((_viewModel.L["MenuRemoveFromTopic"], () => _viewModel.MoveSessionsToTopic(ids, unsorted), false));
        }

        actions.Add((_viewModel.L["MenuMoveToNewTopic"], () =>
        {
            Execute(_viewModel.CreateTopicCommand);
            _viewModel.MoveSessionsToTopic(ids, _viewModel.SelectedTopicItem);
        }, false));
        ShowActions(MobileText.Get("ChooseTopic"), actions);
    }

    /// <summary>«⋯» next to the search: select several sessions; for a group also rename and delete it.</summary>
    private void ListMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        var actions = new List<(string, Action, bool)>
        {
            (_viewModel.L["SelectSessions"], () => Execute(_viewModel.ToggleSelectionModeCommand), false)
        };
        if (_viewModel.IsTopicSelected)
        {
            actions.AddRange(GroupActions());
        }

        ShowActions(_viewModel.LibraryTitle, actions);
    }

    private IEnumerable<(string, Action, bool)> GroupActions() =>
    [
        (_viewModel.L["TopicRename"], () => Execute(_viewModel.StartRenameTopicCommand), false),
        (_viewModel.L["TopicTabMeetings"], () => _viewModel.IsTopicTabMeetings = true, false),
        (_viewModel.L["TopicDelete"], () => RunConfirmable(() => Execute(_viewModel.DeleteTopicCommand)), true)
    ];

    // ===== Press and hold =====

    /// <summary>A session card, a group chip, a hint or a phrase of the text was held: its actions.</summary>
    private void OnLongPress(Control target)
    {
        switch (target.DataContext)
        {
            case SessionSummary session when target is Button:
                ShowSessionActions(session);
                break;
            case TopicItemViewModel { IsTopic: true } group:
                _viewModel.SelectedTopicItem = group;
                ShowActions(group.Name, GroupActions());
                break;
            case PhoneChatItem { IsAnswer: true } hint:
                ShowActions(MobileText.Get("TabAssist"),
                [
                    (_viewModel.L["CopyText"], () => CopyText(hint.Message), false),
                    (MobileText.Get("Share"), () => PhonePlatform.ShareText(_viewModel.CurrentSessionTitle, hint.Message), false)
                ]);
                break;
            case SessionTranscriptItem line:
                ShowActions(MobileText.Get("TabText"),
                [
                    (MobileText.Get("CopyLine"), () => CopyText(line.Text), false),
                    (MobileText.Get("CopyAll"), () => Execute(_viewModel.CopyTranscriptCommand), false)
                ]);
                break;
        }
    }

    private async void CopyText(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
                _viewModel.ShowFeedback(MobileText.Get("Copied"));
            }
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[ui] Copy failed: " + ex.Message);
        }
    }

    // ===== Knowledge base and questions =====

    private void KnowledgeItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: KnowledgeItemViewModel item })
        {
            return;
        }

        if (_viewModel.IsKnowledgeSelectionMode)
        {
            item.IsChecked = !item.IsChecked;
            return;
        }

        _viewModel.SelectedKnowledgeItem = item;
        KnowledgeSheet.IsVisible = true;
    }

    private void CloseKnowledgeSheet_OnClick(object? sender, RoutedEventArgs e) => KnowledgeSheet.IsVisible = false;

    private void AddNote_OnClick(object? sender, RoutedEventArgs e) => RunWithNoteEditor(() => Execute(_viewModel.AddKnowledgeNoteCommand));

    private void EditNote_OnClick(object? sender, RoutedEventArgs e) => RunWithNoteEditor(() => Execute(_viewModel.EditKnowledgeNoteCommand));

    private void RemoveKnowledgeItem_OnClick(object? sender, RoutedEventArgs e)
    {
        KnowledgeItemViewModel? item = _viewModel.SelectedKnowledgeItem;
        RunConfirmable(() =>
        {
            Execute(_viewModel.RemoveKnowledgeItemCommand, item);
            if (!_viewModel.KnowledgeItems.Contains(item!))
            {
                KnowledgeSheet.IsVisible = false;
            }
        });
    }

    private void RemoveCheckedKnowledge_OnClick(object? sender, RoutedEventArgs e) =>
        RunConfirmable(() => Execute(_viewModel.RemoveCheckedKnowledgeCommand));

    private void RemoveCheckedQuestions_OnClick(object? sender, RoutedEventArgs e) =>
        RunConfirmable(() => Execute(_viewModel.RemoveCheckedQuestionsCommand));

    private void RemoveQuestion_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TopicQuestionItemViewModel question })
        {
            RunConfirmable(() => Execute(_viewModel.RemoveQuestionCommand, question));
        }
    }
}

/// <summary>A line of the hints list: the user's request, Replio's answer, «Думаю…», or a quiet note (a hint did not come).</summary>
public sealed class PhoneChatItem
{
    private enum ItemKind
    {
        Request,
        Answer,
        Thinking,
        Notice
    }

    private readonly ItemKind _kind;

    private PhoneChatItem(ItemKind kind, string message)
    {
        _kind = kind;
        Message = message;
    }

    public string Message { get; }
    public bool IsUser => _kind == ItemKind.Request;
    public bool IsAnswer => _kind == ItemKind.Answer;
    public bool IsThinking => _kind == ItemKind.Thinking;
    public bool IsNotice => _kind == ItemKind.Notice;

    public static PhoneChatItem Request(string text) => new(ItemKind.Request, text);
    public static PhoneChatItem Answer(string text) => new(ItemKind.Answer, text);
    public static PhoneChatItem Thinking() => new(ItemKind.Thinking, string.Empty);
    public static PhoneChatItem Notice(string text) => new(ItemKind.Notice, text);
}
