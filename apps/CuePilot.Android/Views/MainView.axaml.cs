using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CuePilot.Android.Platform;
using CuePilotDesktop.Models;
using CuePilotDesktop.Services;
using CuePilotDesktop.ViewModels;

namespace CuePilot.Android.Views;

/// <summary>
/// The phone screen: the same view model as on Windows and macOS, laid out in one column.
/// Dialogs and action sheets are in MainView.Dialogs.cs; the hint dialog of a recording is the desktop overlay's.
/// </summary>
public partial class MainView : UserControl
{
    private const int MaxChatItems = 100;
    private readonly MainViewModel _viewModel = null!;
    private readonly CaptureOrchestratorService _capture = null!;
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
        _viewModel = new MainViewModel(dataRoot, _capture, new NoVisualContextCapture(), diagnostics, settingsService, localizationService);

        // No screen of other apps to capture; without whisper.cpp (a 32-bit phone) speech goes through the API only.
        _viewModel.CaptureScreenContextEnabled = false;
        if (!localWhisper)
        {
            _viewModel.UseApiStt = true;
            UseApiSttBox.IsVisible = false;
            SpeechModelsSection.IsVisible = false;
            SttHintText.Text = MobileText.Get("PhoneApiOnlyHint");
        }

        PhonePlatform.SetHiddenFromScreenCapture(!_viewModel.ShowInScreenShare);
        UpdateHardwareText();

        _viewModel.PlatformPermissionCheckRequested += () => PhonePlatform.HasMicrophonePermission();
        _viewModel.ConfirmationRequested += OnConfirmationRequested;
        _viewModel.NotificationRequested += (title, message, isError) => OnUi(() => ShowMessage(title, message));
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
                case nameof(MainViewModel.SelectedUiLanguage):
                    OnUi(() =>
                    {
                        MobileText.Language = _viewModel.L.CurrentLanguageCode;
                        ApplyQuickActionLabels();
                        UpdateHardwareText();
                    });
                    break;
                case nameof(MainViewModel.ShowInScreenShare):
                    PhonePlatform.SetHiddenFromScreenCapture(!_viewModel.ShowInScreenShare);
                    break;
                case nameof(MainViewModel.IsEditingTopicName) when _viewModel.IsEditingTopicName:
                    Dispatcher.UIThread.Post(() =>
                    {
                        TopicNameEditTextBox.Focus();
                        TopicNameEditTextBox.SelectAll();
                    }, DispatcherPriority.Input);
                    break;
                case nameof(MainViewModel.IsEditingSessionTitle) when _viewModel.IsEditingSessionTitle:
                    Dispatcher.UIThread.Post(() =>
                    {
                        SessionTitleEditTextBox.Focus();
                        SessionTitleEditTextBox.SelectAll();
                    }, DispatcherPriority.Input);
                    break;
            }
        };

        DataContext = _viewModel;
        AssistList.ItemsSource = _chat;
        ResetChat();
        ApplyQuickActionLabels();

        TopicTitleText.PointerPressed += (_, _) => Execute(_viewModel.StartRenameTopicCommand);
        TopicNameEditTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _viewModel.CommitTopicRename();
            }
        };
        TopicNameEditTextBox.LostFocus += (_, _) => _viewModel.CommitTopicRename();
        SessionTitleText.PointerPressed += (_, _) => Execute(_viewModel.StartRenameSessionCommand);
        SessionTitleEditTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Execute(_viewModel.SaveSessionTitleCommand);
            }
        };
        SessionTitleEditTextBox.LostFocus += (_, _) => Execute(_viewModel.SaveSessionTitleCommand);
        AskTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Ask();
            }
        };

        UpdatePage();
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
        global::Android.Util.Log.Info("CuePilot", available
            ? "whisper.cpp: " + LocalWhisper.Description
            : "whisper.cpp is not available: " + LocalWhisper.LoadError);
        try
        {
            if (available && !File.Exists(marker))
            {
                File.WriteAllText(marker, "whisper.cpp is built into CuePilot for Android (libcuepilot_whisper).\n");
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
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CuePilot", "app");
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
        BackButton.IsVisible = _viewModel.SelectedMainPage != "Home";
        KnowledgeSheet.IsVisible = false;
        UpdateSessionPanels();
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

    /// <summary>The last phrases are recognized before the session is closed and summarized.</summary>
    private async void StopRecording_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        StopButton.IsEnabled = false;
        RecordingStatusText.Text = MobileText.Get("Finishing");
        try
        {
            await _capture.StopAndDrainAsync(TimeSpan.FromSeconds(25));
            Execute(_viewModel.StopCaptureCommand);
        }
        finally
        {
            _stopping = false;
            StopButton.IsEnabled = true;
            RecordingStatusText.Text = MobileText.Get("Recording");
        }
    }

    // ===== Session page =====

    private bool IsLiveSession => _viewModel.IsCapturing && _viewModel.SelectedSession?.Status == SessionStatus.Active;

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

    private void UpdateSessionPanels()
    {
        string tab = _assistTab ? "Assist" : _viewModel.SelectedSessionTab;
        AssistList.IsVisible = tab == "Assist";
        SummaryPanel.IsVisible = tab == "Summary";
        TranscriptPanel.IsVisible = tab == "Transcript";
        EmailPanel.IsVisible = tab == "Email";
        UsagePanel.IsVisible = tab == "Usage";
        AskBar.IsVisible = tab == "Assist";
        TabAssist.IsChecked = tab == "Assist";
        TabSummary.IsChecked = tab == "Summary";
        TabTranscript.IsChecked = tab == "Transcript";
        TabEmail.IsChecked = tab == "Email";
        TabUsage.IsChecked = tab == "Usage";
        RecordingBar.IsVisible = IsLiveSession;
        if (TranscriptPanel.IsVisible && _viewModel.SelectedSessionTranscript.Count > 0)
        {
            Dispatcher.UIThread.Post(() => TranscriptList.ScrollIntoView(_viewModel.SelectedSessionTranscript[^1]), DispatcherPriority.Background);
        }
    }

    private void ApplyQuickActionLabels()
    {
        WhatToSayButton.Content = ActionLabel("OverlayActionWhatToSay");
        WhatToAskButton.Content = ActionLabel("OverlayActionWhatShouldISay");
        FollowUpButton.Content = ActionLabel("OverlayActionFollowUpQuestions");
        RecapButton.Content = ActionLabel("OverlayActionRecap");
    }

    private string ActionLabel(string key) => (_viewModel.L[key] ?? string.Empty).Trim().TrimStart('•').TrimStart();

    private void QuickAction_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } button)
        {
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
            case "FollowUp":
                Execute(_viewModel.FollowUpQuestionCommand);
                break;
            case "Recap":
                Execute(_viewModel.RecapCommand);
                break;
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

        _viewModel.OverlayPrompt = question;
        AskTextBox.Text = string.Empty;
        Execute(_viewModel.WhatShouldISayCommand);
    }

    private void ResetChat()
    {
        _chat.Clear();
        _chat.Add(new PhoneChatItem(_viewModel.L["OverlayRoleAssistant"], _viewModel.L["OverlaySuggestionPlaceholder"], isUser: false, isPlaceholder: true));
    }

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
        if (last is { IsPlaceholder: false } && last.IsUser == isUser && last.Message == normalized)
        {
            return;
        }

        if (_chat.Count == 1 && _chat[0].IsPlaceholder)
        {
            _chat.Clear();
        }

        _chat.Add(new PhoneChatItem(isUser ? _viewModel.L["OverlayRoleUser"] : _viewModel.L["OverlayRoleAssistant"], normalized, isUser));
        if (isUser)
        {
            _chat.Add(new PhoneChatItem(_viewModel.L["OverlayRoleAssistant"], "…", isUser: false, isPlaceholder: true, isThinking: true));
        }

        while (_chat.Count > MaxChatItems)
        {
            _chat.RemoveAt(0);
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
            (_viewModel.L["TopicRename"], () => Execute(_viewModel.StartRenameSessionCommand), false),
            (_viewModel.L["FollowUpEmail"], () =>
            {
                _assistTab = false;
                _viewModel.SelectedSessionTab = "Email";
                Execute(_viewModel.FollowUpEmailCommand);
                UpdateSessionPanels();
            }, false)
        };
        if (session.Status == SessionStatus.Completed)
        {
            actions.Add((MobileText.Get("ChooseTopic"), () => ShowMoveSheet(_viewModel.GetActionSessionIds(session), session.TopicId), false));
            actions.Add((_viewModel.L["MenuDeleteSession"], () => RunConfirmable(() => Execute(_viewModel.DeleteSessionCommand, session)), true));
        }

        ShowActions(session.Title, actions);
    }

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

    private void SessionMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionSummary session })
        {
            return;
        }

        ShowActions(session.Title,
        [
            (_viewModel.L["MenuOpenSession"], () => Execute(_viewModel.OpenSessionCommand, session), false),
            (MobileText.Get("ChooseTopic"), () => ShowMoveSheet([session.Id], session.TopicId), false),
            (_viewModel.L["MenuDeleteSession"], () => RunConfirmable(() => Execute(_viewModel.DeleteSessionCommand, session)), true)
        ]);
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

    private void TopicMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        ShowActions(_viewModel.LibraryTitle,
        [
            (_viewModel.L["TopicRename"], () => Execute(_viewModel.StartRenameTopicCommand), false),
            (_viewModel.L["TopicDelete"], () => RunConfirmable(() => Execute(_viewModel.DeleteTopicCommand)), true)
        ]);
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

/// <summary>A message of the hint dialog.</summary>
public sealed class PhoneChatItem(string role, string message, bool isUser, bool isPlaceholder = false, bool isThinking = false)
{
    public string Role { get; } = role;
    public string Message { get; } = message;
    public bool IsUser { get; } = isUser;
    public bool IsPlaceholder { get; } = isPlaceholder;
    public bool IsThinking { get; } = isThinking;
}

/// <summary>No screen snapshots on the phone.</summary>
internal sealed class NoVisualContextCapture : IVisualContextCapture
{
    public event EventHandler<SessionVisualFrame>? KeyFrameCaptured { add { } remove { } }
    public event EventHandler<string>? LogLine { add { } remove { } }

    public string StorageRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CuePilot", "captures");

    public void Start(string sessionId, int intervalMs)
    {
    }

    public void Stop()
    {
    }

    public bool CaptureSnapshot(string source, bool force, out SessionVisualFrame? frame)
    {
        frame = null;
        return false;
    }

    public void DeleteFrameFiles(IEnumerable<SessionVisualFrame> frames)
    {
    }

    public void DeleteSessionFiles(string sessionId)
    {
    }

    public void CleanupOrphans(IReadOnlyCollection<SessionVisualFrame> referencedFrames, IReadOnlyCollection<string> knownSessionIds)
    {
    }
}
