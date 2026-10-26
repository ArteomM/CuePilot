using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using CuePilotDesktop.Models;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly string _projectRoot;
    private readonly CaptureOrchestratorService _captureOrchestrator;
    private readonly IVisualContextCapture _visualContextCapture;
    private readonly ISettingsService _settingsService;
    private readonly SessionLifecycleService _sessionLifecycleService;
    private readonly ConversationAssistService _conversationAssistService;
    private readonly FileLogSink _logSink = new();
    private readonly SecretsProvider _secrets;
    private readonly SynchronizationContext _uiContext;

    private AppSettings _settings;
    private bool _permissionsConfirmed;
    private bool _isCapturing;
    private string _selectedWindowMode = "Expanded";
    private string _selectedSessionMode = "Meeting";
    private string _toggleOverlayHotkeyText = string.Empty;
    private string _toggleCaptureHotkeyText = string.Empty;
    private string _selectedUiLanguage = "ru";
    private string _selectedThemeMode = "dark";
    private string _captureStatusText = string.Empty;
    private string _privacyBadgeText = string.Empty;
    private string _privacyBadgeBrush = DefaultIdleBrush;
    private string _systemStatsText = string.Empty;
    private string _micStatsText = string.Empty;
    private string _tokenUsageText = string.Empty;
    private string _overlayPrompt = string.Empty;
    private string _bestResponseText = string.Empty;
    private string _alternativeResponseText = string.Empty;
    private string _followUpText = string.Empty;
    private string _keyPointsText = string.Empty;
    private string _contextSummaryText = string.Empty;
    private string _overlayStateText = string.Empty;
    private string _audioHealthMessage = string.Empty;
    private double _audioMeterLevel;
    private int _onboardingStepIndex;
    private bool _showInScreenShare;
    private bool _showInTaskbar = true;
    private bool _invisibilityModeEnabled = true;
    private bool _autoLaunchEnabled;
    private bool _captureScreenContextEnabled = true;
    private bool _videoContextEnabled = true;
    private int _videoContextIntervalMs = 2000;
    private int _visualContextMaxFrames = 24;
    private int _visualContextMaxImagesPerRequest = 3;
    private string _visualContextStatusText = string.Empty;
    private string _lastVisualCaptureText = string.Empty;
    private int _visualFramesCount;
    private string _selectedMonitor = "Primary";
    private string _selectedOutputLanguage = "ru";
    private string _selectedMainPage = "Home";
    private string _selectedSessionTab = "Summary";
    private SessionSummary? _selectedSession;
    private string _currentSessionId = string.Empty;
    private string _currentSessionTitle = string.Empty;
    private string _editableSessionTitle = string.Empty;
    private bool _isEditingSessionTitle;
    private string _currentSessionSummary = string.Empty;
    private SessionUsageView _usageView = SessionUsageView.Empty;
    private string _selectedModeName = "General";
    private string _modeEditorName = AppSettings.GeneralModeName;
    private string _generalRealtimePrompt = string.Empty;
    private string _generalNotesTemplate = string.Empty;
    private string _generalReferenceFile = string.Empty;
    private string _defaultRealtimePrompt = string.Empty;
    private string _defaultNotesTemplate = string.Empty;
    private string _defaultReferenceFile = string.Empty;
    private string _sessionQuery = string.Empty;
    private bool _smartModeEnabled;
    private string _selectedApiPreset = "Custom";
    private string _apiProvider = "OpenAI Compatible";
    private string _apiKeyHeaderName = "Authorization";
    private string _apiKeyPrefix = "Bearer";
    private string _chatCompletionsPath = "/chat/completions";
    private string _openAiApiKey = string.Empty;
    private string _openAiModel = AppSettings.PolzaFastModel;
    private string _analysisModel = AppSettings.PolzaAnalysisModel;
    private string _openAiBaseUrl = AppSettings.PolzaBaseUrl;
    private bool _useApiStt;
    private string _sttApiModel = AppSettings.PolzaSttModel;
    private string _sttApiKey = string.Empty;
    private string _sttGlossary = string.Empty;
    private CancellationTokenSource? _speechModelDownloadCts;
    private string _sttStatusText = string.Empty;
    private double _lastSttCostSeen;
    private DateTime _lastSttLagWarningAt = DateTime.MinValue;
    private ObservableCollection<SessionTranscriptItem> _selectedSessionTranscript = [];
    private string _askHotkeyText = string.Empty;
    private string _clearChatHotkeyText = string.Empty;
    private string _moveLeftHotkeyText = string.Empty;
    private string _moveRightHotkeyText = string.Empty;
    private string _moveUpHotkeyText = string.Empty;
    private string _moveDownHotkeyText = string.Empty;
    private string _scrollUpHotkeyText = string.Empty;
    private string _scrollDownHotkeyText = string.Empty;
    private string _snapshotHotkeyText = string.Empty;
    private bool _isAssistBusy;
    private bool _isWhatsNewOpen;
    private static readonly string[] ApiPresetItems =
    [
        AppSettings.PolzaPresetName,
        "Custom",
        "OpenAI",
        "OpenRouter",
        "Groq",
        "Together",
        "LM Studio (local)",
        "vLLM (local)",
        "Ollama (OpenAI-compatible)"
    ];

    private const string DefaultActiveBrush = "#5EF0C1";
    private const string DefaultIdleBrush = "#8A9499";

    private readonly RelayCommand _startCaptureCommand;
    private readonly RelayCommand _stopCaptureCommand;
    private readonly RelayCommand _nextOnboardingCommand;
    private readonly RelayCommand _previousOnboardingCommand;
    private readonly RelayCommand _hideOverlayCommand;
    private readonly RelayCommand _askOverlayCommand;
    private readonly RelayCommand _openSettingsCommand;
    private readonly RelayCommand _openModesCommand;
    private readonly RelayCommand _openSessionCommand;
    private readonly RelayCommand _goHomeCommand;
    private readonly RelayCommand _runAudioCaptureTestCommand;
    private readonly RelayCommand _whatShouldISayCommand;
    private readonly RelayCommand _regenerateSummaryCommand;
    private readonly RelayCommand _copySummaryCommand;
    private readonly RelayCommand _followUpEmailCommand;
    private readonly RelayCommand _followUpQuestionCommand;
    private readonly RelayCommand _suggestNextQuestionCommand;
    private readonly RelayCommand _suggestReplyCommand;
    private readonly RelayCommand _recapCommand;
    private readonly RelayCommand _toggleWhatsNewCommand;
    private readonly RelayCommand _closeWhatsNewCommand;
    private readonly RelayCommand _openActiveSessionCommand;
    private readonly RelayCommand _startRenameSessionCommand;
    private readonly RelayCommand _saveSessionTitleCommand;
    private readonly RelayCommand _deleteSessionCommand;
    private readonly RelayCommand _captureSnapshotCommand;
    private readonly RelayCommand _setModeActiveCommand;
    private readonly RelayCommand _uploadModeReferenceFileCommand;
    private readonly RelayCommand _newModeCommand;

    public MainViewModel(
        string projectRoot,
        CaptureOrchestratorService captureOrchestrator,
        IVisualContextCapture visualContextCapture,
        DiagnosticsViewModel diagnostics,
        ISettingsService settingsService,
        LocalizationService localizer)
    {
        _projectRoot = projectRoot;
        _captureOrchestrator = captureOrchestrator;
        _visualContextCapture = visualContextCapture;
        _settingsService = settingsService;
        _secrets = new SecretsProvider(projectRoot);
        _sessionLifecycleService = new SessionLifecycleService(new JsonSessionStore());
        _sessionLifecycleService.PersistFailed += line => RunOnUi(() => AppendLog(line));
        _conversationAssistService = new ConversationAssistService(
            new OpenAiAssistClient(new HttpClient { Timeout = TimeSpan.FromSeconds(180) }));
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        Diagnostics = diagnostics;
        L = localizer;

        _settings = _settingsService.Load();
        _toggleOverlayHotkeyText = NormalizeSlashHotkey(_settings.ToggleOverlayHotkey, "Ctrl+/");
        _toggleCaptureHotkeyText = NormalizeSlashHotkey(_settings.ToggleCaptureHotkey, "Ctrl+Shift+/");
        _selectedUiLanguage = _settings.UiLanguage;
        _selectedThemeMode = _settings.ThemeMode;
        _showInScreenShare = _settings.ShowInScreenShare;
        _showInTaskbar = _settings.ShowInTaskbar;
        _invisibilityModeEnabled = _settings.InvisibilityModeEnabled;
        _autoLaunchEnabled = _settings.AutoLaunchEnabled;
        _permissionsConfirmed = _settings.PermissionsConfirmed;
        _captureScreenContextEnabled = _settings.CaptureScreenContextEnabled;
        _selectedMonitor = _settings.SelectedMonitor;
        _selectedOutputLanguage = _settings.OutputLanguage;
        _generalRealtimePrompt = _settings.GeneralRealtimePrompt;
        _generalNotesTemplate = _settings.GeneralNotesTemplate;
        _generalReferenceFile = _settings.GeneralReferenceFile;
        _defaultRealtimePrompt = _settings.DefaultRealtimePrompt;
        _defaultNotesTemplate = _settings.DefaultNotesTemplate;
        _defaultReferenceFile = _settings.DefaultReferenceFile;
        _selectedModeName = string.Equals(_settings.SelectedModeName, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase)
            ? AppSettings.DefaultModeName
            : AppSettings.GeneralModeName;
        _modeEditorName = _selectedModeName;
        EnsureModeExamples();
        _videoContextEnabled = _settings.VideoContextEnabled;
        _videoContextIntervalMs = Math.Clamp(_settings.VideoContextIntervalMs, 800, 20000);
        _visualContextMaxFrames = Math.Clamp(_settings.VisualContextMaxFrames, 4, 120);
        _visualContextMaxImagesPerRequest = Math.Clamp(_settings.VisualContextMaxImagesPerRequest, 1, 5);
        _smartModeEnabled = _settings.SmartModeEnabled;
        _selectedApiPreset = string.IsNullOrWhiteSpace(_settings.ApiPresetName) ? AppSettings.PolzaPresetName : _settings.ApiPresetName;
        _apiProvider = string.IsNullOrWhiteSpace(_settings.ApiProvider) ? AppSettings.PolzaPresetName : _settings.ApiProvider;
        _apiKeyHeaderName = string.IsNullOrWhiteSpace(_settings.ApiKeyHeaderName) ? "Authorization" : _settings.ApiKeyHeaderName;
        _apiKeyPrefix = string.IsNullOrWhiteSpace(_settings.ApiKeyPrefix) ? "Bearer" : _settings.ApiKeyPrefix;
        _chatCompletionsPath = string.IsNullOrWhiteSpace(_settings.ChatCompletionsPath) ? "/chat/completions" : _settings.ChatCompletionsPath;
        _openAiApiKey = _settings.OpenAiApiKey;
        _openAiModel = _settings.OpenAiModel;
        _analysisModel = _settings.AnalysisModel ?? string.Empty;
        _openAiBaseUrl = _settings.OpenAiBaseUrl;
        _useApiStt = string.Equals(_settings.SttProvider, AppSettings.SttProviderApi, StringComparison.OrdinalIgnoreCase);
        _sttApiModel = string.IsNullOrWhiteSpace(_settings.SttApiModel) ? AppSettings.PolzaSttModel : _settings.SttApiModel;
        _sttApiKey = _settings.SttApiKey ?? string.Empty;
        _sttGlossary = _settings.SttGlossary ?? string.Empty;
        if (!ApiPresetItems.Contains(_selectedApiPreset))
        {
            _selectedApiPreset = InferApiPresetFromUrl(_openAiBaseUrl);
        }
        _askHotkeyText = _settings.AskHotkey;
        _clearChatHotkeyText = _settings.ClearChatHotkey;
        _moveLeftHotkeyText = _settings.MoveLeftHotkey;
        _moveRightHotkeyText = _settings.MoveRightHotkey;
        _moveUpHotkeyText = _settings.MoveUpHotkey;
        _moveDownHotkeyText = _settings.MoveDownHotkey;
        _scrollUpHotkeyText = _settings.ScrollUpHotkey;
        _scrollDownHotkeyText = _settings.ScrollDownHotkey;
        _snapshotHotkeyText = string.IsNullOrWhiteSpace(_settings.SnapshotHotkey) ? "Ctrl+Shift+S" : _settings.SnapshotHotkey;
        if (string.Equals(_toggleCaptureHotkeyText, _snapshotHotkeyText, StringComparison.OrdinalIgnoreCase))
        {
            _toggleCaptureHotkeyText = "Ctrl+Shift+/";
        }

        SessionHistory = [];
        QuickActions = [];
        ApiProviderPresets = new ObservableCollection<string>(ApiPresetItems);
        UiStates = [];
        CompletedSessions = new ObservableCollection<SessionSummary>();
        _completedSessionsView = CreateFilteredListView(CompletedSessions, FilterSessionItem, nameof(SessionSummary.DayGroupLabel));

        _startCaptureCommand = new RelayCommand(StartCapture, () => !IsCapturing);
        _stopCaptureCommand = new RelayCommand(async () => await StopCaptureAsync(), () => IsCapturing);
        _nextOnboardingCommand = new RelayCommand(NextOnboardingStep, () => OnboardingStepIndex < 4);
        _previousOnboardingCommand = new RelayCommand(PreviousOnboardingStep, () => OnboardingStepIndex > 0);
        _hideOverlayCommand = new RelayCommand(() => HideOverlayRequested?.Invoke());
        _askOverlayCommand = new RelayCommand(() => ShowOverlayRequested?.Invoke());
        _openSettingsCommand = new RelayCommand(() => SelectedMainPage = "Settings");
        _openModesCommand = new RelayCommand(() =>
        {
            ModeEditorName = SelectedModeName;
            SelectedMainPage = "Modes";
        });
        _openSessionCommand = new RelayCommand(OpenSession);
        _goHomeCommand = new RelayCommand(() => SelectedMainPage = "Home", () => SelectedMainPage != "Home");
        _runAudioCaptureTestCommand = new RelayCommand(RunAudioCaptureTest);
        _whatShouldISayCommand = new RelayCommand(async () => await RunWhatShouldISayAsync(), () => !IsAssistBusy);
        _regenerateSummaryCommand = new RelayCommand(async () => await RegenerateSummaryAsync(), () => !IsAssistBusy);
        _copySummaryCommand = new RelayCommand(CopySummaryToClipboard);
        _followUpEmailCommand = new RelayCommand(async () => await GenerateFollowUpEmailAsync(), () => !IsAssistBusy);
        _followUpQuestionCommand = new RelayCommand(async () => await GenerateFollowUpQuestionAsync(), () => !IsAssistBusy);
        _suggestNextQuestionCommand = new RelayCommand(async () => await SuggestNextQuestionAsync(), () => !IsAssistBusy);
        _suggestReplyCommand = new RelayCommand(async () => await SuggestReplyAsync(), () => !IsAssistBusy);
        _recapCommand = new RelayCommand(async () => await GenerateRecapAsync(), () => !IsAssistBusy);
        _toggleWhatsNewCommand = new RelayCommand(() => IsWhatsNewOpen = !IsWhatsNewOpen);
        _closeWhatsNewCommand = new RelayCommand(() => IsWhatsNewOpen = false);
        _openActiveSessionCommand = new RelayCommand(OpenActiveSession);
        _startRenameSessionCommand = new RelayCommand(StartRenameSessionTitle, () => SelectedSession != null);
        _saveSessionTitleCommand = new RelayCommand(SaveSessionTitle, () => IsEditingSessionTitle && SelectedSession != null);
        _deleteSessionCommand = new RelayCommand(DeleteSession);
        _captureSnapshotCommand = new RelayCommand(CaptureSnapshotNow, () => IsCapturing && CaptureScreenContextEnabled);
        _setModeActiveCommand = new RelayCommand(SetModeActive, () => !IsEditorModeActive);
        _uploadModeReferenceFileCommand = new RelayCommand(UploadModeReferenceFile);
        _newModeCommand = new RelayCommand(RequestNewMode);

        StartCaptureCommand = _startCaptureCommand;
        StopCaptureCommand = _stopCaptureCommand;
        RefreshDiagnosticsCommand = new RelayCommand(RefreshDiagnostics);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        SpeechModels = new ObservableCollection<SpeechModelItemViewModel>(
            SpeechModelCatalog.Models.Select(model => new SpeechModelItemViewModel(model, L)));
        DownloadSpeechModelCommand = new RelayCommand(DownloadSpeechModel, _ => _speechModelDownloadCts == null);
        CancelSpeechModelDownloadCommand = new RelayCommand(() => _speechModelDownloadCts?.Cancel());
        UseSpeechModelCommand = new RelayCommand(UseSpeechModel);
        DeleteSpeechModelCommand = new RelayCommand(DeleteSpeechModel);
        RefreshSpeechModels();
        ToggleOverlayCommand = new RelayCommand(() => ToggleOverlayRequested?.Invoke());
        ToggleCaptureCommand = new RelayCommand(ToggleCaptureFromHotkey);
        NextOnboardingCommand = _nextOnboardingCommand;
        PreviousOnboardingCommand = _previousOnboardingCommand;
        CompleteOnboardingCommand = new RelayCommand(() => OnboardingStepIndex = 4);
        ApplyQuickActionCommand = new RelayCommand(ApplyQuickAction);
        ResetSessionContextCommand = new RelayCommand(ResetSessionContext);
        RunAudioTestCommand = new RelayCommand(() => AudioHealthMessage = L["AudioHealthListening"]);
        UpdateAndRestartCommand = new RelayCommand(() => AppendLog("[update] Update and restart requested."));
        HideOverlayCommand = _hideOverlayCommand;
        AskOverlayCommand = _askOverlayCommand;
        OpenSettingsCommand = _openSettingsCommand;
        OpenModesCommand = _openModesCommand;
        OpenSessionCommand = _openSessionCommand;
        GoHomeCommand = _goHomeCommand;
        RunAudioCaptureTestCommand = _runAudioCaptureTestCommand;
        WhatShouldISayCommand = _whatShouldISayCommand;
        RegenerateSummaryCommand = _regenerateSummaryCommand;
        CopySummaryCommand = _copySummaryCommand;
        FollowUpEmailCommand = _followUpEmailCommand;
        FollowUpQuestionCommand = _followUpQuestionCommand;
        SuggestNextQuestionCommand = _suggestNextQuestionCommand;
        SuggestReplyCommand = _suggestReplyCommand;
        RecapCommand = _recapCommand;
        ToggleWhatsNewCommand = _toggleWhatsNewCommand;
        CloseWhatsNewCommand = _closeWhatsNewCommand;
        OpenActiveSessionCommand = _openActiveSessionCommand;
        StartRenameSessionCommand = _startRenameSessionCommand;
        SaveSessionTitleCommand = _saveSessionTitleCommand;
        DeleteSessionCommand = _deleteSessionCommand;
        CaptureSnapshotCommand = _captureSnapshotCommand;
        SetModeActiveCommand = _setModeActiveCommand;
        UploadModeReferenceFileCommand = _uploadModeReferenceFileCommand;
        NewModeCommand = _newModeCommand;

        _captureOrchestrator.LogLine += (_, line) => RunOnUi(() => AppendLog(line));
        _captureOrchestrator.StatsUpdated += (_, stats) => RunOnUi(() => OnStatsUpdated(stats));
        _captureOrchestrator.TranscriptReceived += (_, transcript) => RunOnUi(() => OnTranscriptReceived(transcript));
        _captureOrchestrator.RunningStateChanged += (_, running) => RunOnUi(() => OnRunningStateChanged(running));
        _visualContextCapture.KeyFrameCaptured += (_, frame) => RunOnUi(() => OnVisualFrameCaptured(frame));
        _visualContextCapture.LogLine += (_, line) => RunOnUi(() => AppendLog(line));

        L.PropertyChanged += OnLocalizationChanged;
        ApplyLocalization();
        ApplyTheme();
        RebuildLocalizedCollections();
        InitializeModelCatalog();
        InitializeLibrary();
        RefreshSessionList();
        CleanupVisualContextInBackground();
    }

    public event Action? ToggleOverlayRequested;
    public event Action? ShowOverlayRequested;
    public event Action? HideOverlayRequested;
    public event Action<string>? WindowModeRequested;
    public event Action? HotkeysChanged;
    public event Action<bool>? OverlayCaptureStateChanged;
    public event Action<string>? OverlaySuggestionChanged;
    public event Action<string>? OverlayQuestionAsked;
    public event Action? OverlayAnswerFailed;
    public event Action<string>? OverlayStatsChanged;
    public event Action<string>? OverlayNoticeRequested;
    public event Action? MainWindowAttentionRequested;
    public event Action<int, int>? OverlayMoveRequested;
    public event Func<bool>? PermissionConfirmationRequested;

    /// <summary>
    /// A platform that can read its permissions itself (macOS) checks them before every recording instead of the
    /// one-time permission wizard; false cancels the start. Not used on Windows.
    /// </summary>
    public event Func<bool>? PlatformPermissionCheckRequested;
    public event Func<string, string, bool>? ConfirmationRequested;
    public event Action<string, string, bool>? NotificationRequested;

    public LocalizationService L { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public ObservableCollection<SessionTranscriptItem> SelectedSessionTranscript
    {
        get => _selectedSessionTranscript;
        private set
        {
            _selectedSessionTranscript = value;
            OnPropertyChanged();
        }
    }
    public ObservableCollection<string> QuickActions { get; }
    public ObservableCollection<string> ApiProviderPresets { get; }
    public ObservableCollection<string> SessionHistory { get; }
    public ObservableCollection<UiStateItem> UiStates { get; }
    public ObservableCollection<SessionSummary> CompletedSessions { get; }
    private readonly IFilteredListView _completedSessionsView;

    /// <summary>Completed sessions filtered by topic and search, grouped by day (the platform's collection view).</summary>
    public object CompletedSessionsView => _completedSessionsView.View;

    public RelayCommand StartCaptureCommand { get; }
    public RelayCommand StopCaptureCommand { get; }
    public RelayCommand RefreshDiagnosticsCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand DownloadSpeechModelCommand { get; }
    public RelayCommand CancelSpeechModelDownloadCommand { get; }
    public RelayCommand UseSpeechModelCommand { get; }
    public RelayCommand DeleteSpeechModelCommand { get; }
    public RelayCommand ToggleOverlayCommand { get; }
    public RelayCommand ToggleCaptureCommand { get; }
    public RelayCommand NextOnboardingCommand { get; }
    public RelayCommand PreviousOnboardingCommand { get; }
    public RelayCommand CompleteOnboardingCommand { get; }
    public RelayCommand ApplyQuickActionCommand { get; }
    public RelayCommand ResetSessionContextCommand { get; }
    public RelayCommand RunAudioTestCommand { get; }
    public RelayCommand UpdateAndRestartCommand { get; }
    public RelayCommand HideOverlayCommand { get; }
    public RelayCommand AskOverlayCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenModesCommand { get; }
    public RelayCommand OpenSessionCommand { get; }
    public RelayCommand GoHomeCommand { get; }
    public RelayCommand RunAudioCaptureTestCommand { get; }
    public RelayCommand WhatShouldISayCommand { get; }
    public RelayCommand RegenerateSummaryCommand { get; }
    public RelayCommand CopySummaryCommand { get; }
    public RelayCommand FollowUpEmailCommand { get; }
    public RelayCommand FollowUpQuestionCommand { get; }
    public RelayCommand SuggestNextQuestionCommand { get; }
    public RelayCommand SuggestReplyCommand { get; }
    public RelayCommand RecapCommand { get; }
    public RelayCommand ToggleWhatsNewCommand { get; }
    public RelayCommand CloseWhatsNewCommand { get; }
    public RelayCommand OpenActiveSessionCommand { get; }
    public RelayCommand StartRenameSessionCommand { get; }
    public RelayCommand SaveSessionTitleCommand { get; }
    public RelayCommand DeleteSessionCommand { get; }
    public RelayCommand CaptureSnapshotCommand { get; }
    public RelayCommand SetModeActiveCommand { get; }
    public RelayCommand UploadModeReferenceFileCommand { get; }
    public RelayCommand NewModeCommand { get; }

    public bool IsAssistBusy
    {
        get => _isAssistBusy;
        private set
        {
            if (_isAssistBusy == value)
            {
                return;
            }

            _isAssistBusy = value;
            OnPropertyChanged();
            _whatShouldISayCommand.RaiseCanExecuteChanged();
            _regenerateSummaryCommand.RaiseCanExecuteChanged();
            _followUpEmailCommand.RaiseCanExecuteChanged();
            _followUpQuestionCommand.RaiseCanExecuteChanged();
            _suggestNextQuestionCommand.RaiseCanExecuteChanged();
            _suggestReplyCommand.RaiseCanExecuteChanged();
            _recapCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Version of the running build (InformationalVersion in the project), so the pill never goes stale.</summary>
    public static string AppVersion { get; } =
        (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0]
        ?? "1.0";

    public string WhatsNewButtonText => string.Format(L["WhatsNewButtonText"], AppVersion);

    public string WhatsNewTitle => string.Format(L["WhatsNewTitle"], AppVersion);

    public bool IsWhatsNewOpen
    {
        get => _isWhatsNewOpen;
        set
        {
            if (_isWhatsNewOpen == value)
            {
                return;
            }

            _isWhatsNewOpen = value;
            OnPropertyChanged();
        }
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (_isCapturing == value)
            {
                return;
            }

            _isCapturing = value;
            OnPropertyChanged();
            _startCaptureCommand.RaiseCanExecuteChanged();
            _stopCaptureCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectedWindowMode
    {
        get => _selectedWindowMode;
        set
        {
            if (_selectedWindowMode == value)
            {
                return;
            }

            _selectedWindowMode = value;
            OnPropertyChanged();
            WindowModeRequested?.Invoke(value);
        }
    }

    public string SelectedSessionMode
    {
        get => _selectedSessionMode;
        set
        {
            if (_selectedSessionMode == value)
            {
                return;
            }

            _selectedSessionMode = value;
            OnPropertyChanged();
        }
    }

    public string ToggleOverlayHotkeyText
    {
        get => _toggleOverlayHotkeyText;
        set
        {
            if (_toggleOverlayHotkeyText == value)
            {
                return;
            }

            _toggleOverlayHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public string ToggleCaptureHotkeyText
    {
        get => _toggleCaptureHotkeyText;
        set
        {
            if (_toggleCaptureHotkeyText == value)
            {
                return;
            }

            _toggleCaptureHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public string AskHotkeyText
    {
        get => _askHotkeyText;
        set
        {
            if (_askHotkeyText == value) return;
            _askHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public string ClearChatHotkeyText
    {
        get => _clearChatHotkeyText;
        set
        {
            if (_clearChatHotkeyText == value) return;
            _clearChatHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public string MoveLeftHotkeyText
    {
        get => _moveLeftHotkeyText;
        set
        {
            if (_moveLeftHotkeyText == value) return;
            _moveLeftHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public string MoveRightHotkeyText
    {
        get => _moveRightHotkeyText;
        set
        {
            if (_moveRightHotkeyText == value) return;
            _moveRightHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public string MoveUpHotkeyText
    {
        get => _moveUpHotkeyText;
        set
        {
            if (_moveUpHotkeyText == value) return;
            _moveUpHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public string MoveDownHotkeyText
    {
        get => _moveDownHotkeyText;
        set
        {
            if (_moveDownHotkeyText == value) return;
            _moveDownHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public string ScrollUpHotkeyText
    {
        get => _scrollUpHotkeyText;
        set
        {
            if (_scrollUpHotkeyText == value) return;
            _scrollUpHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public string ScrollDownHotkeyText
    {
        get => _scrollDownHotkeyText;
        set
        {
            if (_scrollDownHotkeyText == value) return;
            _scrollDownHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public string SnapshotHotkeyText
    {
        get => _snapshotHotkeyText;
        set
        {
            if (_snapshotHotkeyText == value) return;
            _snapshotHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public bool SmartModeEnabled
    {
        get => _smartModeEnabled;
        set
        {
            if (_smartModeEnabled == value)
            {
                return;
            }

            _smartModeEnabled = value;
            OnPropertyChanged();
        }
    }

    public string SelectedApiPreset
    {
        get => _selectedApiPreset;
        set
        {
            if (_selectedApiPreset == value)
            {
                return;
            }

            _selectedApiPreset = value;
            OnPropertyChanged();
            NotifyApiPresetDependentProperties();
            ApplyApiPreset(value);
            RebuildModelOptions(ModelKind.Chat);
        }
    }

    public bool ApiKeyFieldVisibility => IsApiKeyRequired();
    public bool ProviderFieldVisibility => IsCustomPreset();
    public bool BaseUrlFieldVisibility => IsCustomPreset();
    public bool EndpointPathFieldVisibility => IsCustomPreset();
    public bool ApiKeyHeaderFieldVisibility => IsCustomPreset();
    public bool ApiKeyPrefixFieldVisibility => IsCustomPreset();

    public string OpenAiApiKey
    {
        get => _openAiApiKey;
        set
        {
            if (_openAiApiKey == value) return;
            _openAiApiKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SttKeySourceText));
        }
    }

    public string ApiProvider
    {
        get => _apiProvider;
        set
        {
            if (_apiProvider == value) return;
            _apiProvider = value;
            OnPropertyChanged();
        }
    }

    public string ApiKeyHeaderName
    {
        get => _apiKeyHeaderName;
        set
        {
            if (_apiKeyHeaderName == value) return;
            _apiKeyHeaderName = value;
            OnPropertyChanged();
        }
    }

    public string ApiKeyPrefix
    {
        get => _apiKeyPrefix;
        set
        {
            if (_apiKeyPrefix == value) return;
            _apiKeyPrefix = value;
            OnPropertyChanged();
        }
    }

    public string ChatCompletionsPath
    {
        get => _chatCompletionsPath;
        set
        {
            if (_chatCompletionsPath == value) return;
            _chatCompletionsPath = value;
            OnPropertyChanged();
        }
    }

    public string OpenAiModel
    {
        get => _openAiModel;
        set
        {
            if (_openAiModel == value) return;
            _openAiModel = value;
            OnPropertyChanged();
        }
    }

    public string AnalysisModel
    {
        get => _analysisModel;
        set
        {
            if (_analysisModel == value) return;
            _analysisModel = value;
            OnPropertyChanged();
        }
    }

    public bool UseApiStt
    {
        get => _useApiStt;
        set
        {
            if (_useApiStt == value) return;
            _useApiStt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SttKeySourceText));
        }
    }

    public string SttApiModel
    {
        get => _sttApiModel;
        set
        {
            if (_sttApiModel == value) return;
            _sttApiModel = value;
            OnPropertyChanged();
        }
    }

    public string SttApiKey
    {
        get => _sttApiKey;
        set
        {
            if (_sttApiKey == value) return;
            _sttApiKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SttKeySourceText));
        }
    }

    /// <summary>Names and terms from the user's calls, passed to speech recognition as a hint.</summary>
    public string SttGlossary
    {
        get => _sttGlossary;
        set
        {
            if (_sttGlossary == value) return;
            _sttGlossary = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Speech models available for download, with a recommendation for each.</summary>
    public ObservableCollection<SpeechModelItemViewModel> SpeechModels { get; }

    public string SpeechModelStatusText
    {
        get
        {
            string? path = SpeechModelCatalog.ResolveActiveModelPath(_settings, _projectRoot);
            if (path == null)
            {
                return L["SpeechModelMissingStatus"];
            }

            string fileName = Path.GetFileName(path);
            string title = SpeechModelCatalog.Find(fileName)?.Title ?? fileName;
            return string.Format(L["SpeechModelActiveStatus"], title);
        }
    }

    public string SpeechModelHardwareText => string.Format(
        L["SpeechModelHardware"],
        SpeechModelCatalog.CpuCores,
        Math.Round(SpeechModelCatalog.TotalRamGb));

    public string SttStatusText
    {
        get => _sttStatusText;
        private set
        {
            if (_sttStatusText == value) return;
            _sttStatusText = value;
            OnPropertyChanged();
        }
    }

    public string LogDirectory => _logSink.LogDirectory;

    /// <summary>Explains in Settings which key speech recognition will use (the key itself is never shown).</summary>
    public string SttKeySourceText => L["SttKeySource" + DescribeSttKeySource()];

    public string OpenAiBaseUrl
    {
        get => _openAiBaseUrl;
        set
        {
            if (_openAiBaseUrl == value) return;
            _openAiBaseUrl = value;
            OnPropertyChanged();
            RebuildModelOptions(ModelKind.Chat);
        }
    }

    public string SelectedUiLanguage
    {
        get => _selectedUiLanguage;
        set
        {
            if (_selectedUiLanguage == value)
            {
                return;
            }

            _selectedUiLanguage = value;
            OnPropertyChanged();
            L.SetLanguage(value);
            ApplyInterfaceLanguage(value);
        }
    }

    public string SelectedThemeMode
    {
        get => _selectedThemeMode;
        set
        {
            if (_selectedThemeMode == value)
            {
                return;
            }

            _selectedThemeMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(IsLightTheme));
            ApplyTheme();
            _settings.ThemeMode = value;
            PersistSettingsQuietly();
        }
    }

    public bool ShowInScreenShare
    {
        get => _showInScreenShare;
        set
        {
            if (_showInScreenShare == value)
            {
                return;
            }

            _showInScreenShare = value;
            OnPropertyChanged();
        }
    }

    public bool ShowInTaskbar
    {
        get => _showInTaskbar;
        set
        {
            if (_showInTaskbar == value)
            {
                return;
            }

            _showInTaskbar = value;
            OnPropertyChanged();
        }
    }

    public bool InvisibilityModeEnabled
    {
        get => _invisibilityModeEnabled;
        set
        {
            if (_invisibilityModeEnabled == value)
            {
                return;
            }

            _invisibilityModeEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool AutoLaunchEnabled
    {
        get => _autoLaunchEnabled;
        set
        {
            if (_autoLaunchEnabled == value)
            {
                return;
            }

            _autoLaunchEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool CaptureScreenContextEnabled
    {
        get => _captureScreenContextEnabled;
        set
        {
            if (_captureScreenContextEnabled == value)
            {
                return;
            }

            _captureScreenContextEnabled = value;
            OnPropertyChanged();
            _captureSnapshotCommand.RaiseCanExecuteChanged();
        }
    }

    public bool VideoContextEnabled
    {
        get => _videoContextEnabled;
        set
        {
            if (_videoContextEnabled == value)
            {
                return;
            }

            _videoContextEnabled = value;
            OnPropertyChanged();
        }
    }

    public int VideoContextIntervalMs
    {
        get => _videoContextIntervalMs;
        set
        {
            int next = Math.Clamp(value, 800, 20000);
            if (_videoContextIntervalMs == next)
            {
                return;
            }

            _videoContextIntervalMs = next;
            OnPropertyChanged();
        }
    }

    public int VisualContextMaxFrames
    {
        get => _visualContextMaxFrames;
        set
        {
            int next = Math.Clamp(value, 4, 120);
            if (_visualContextMaxFrames == next)
            {
                return;
            }

            _visualContextMaxFrames = next;
            OnPropertyChanged();
        }
    }

    public int VisualContextMaxImagesPerRequest
    {
        get => _visualContextMaxImagesPerRequest;
        set
        {
            int next = Math.Clamp(value, 1, 5);
            if (_visualContextMaxImagesPerRequest == next)
            {
                return;
            }

            _visualContextMaxImagesPerRequest = next;
            OnPropertyChanged();
        }
    }

    public string VisualContextStatusText
    {
        get => _visualContextStatusText;
        private set
        {
            if (_visualContextStatusText == value)
            {
                return;
            }

            _visualContextStatusText = value;
            OnPropertyChanged();
        }
    }

    public string LastVisualCaptureText
    {
        get => _lastVisualCaptureText;
        private set
        {
            if (_lastVisualCaptureText == value)
            {
                return;
            }

            _lastVisualCaptureText = value;
            OnPropertyChanged();
        }
    }

    public int VisualFramesCount
    {
        get => _visualFramesCount;
        private set
        {
            if (_visualFramesCount == value)
            {
                return;
            }

            _visualFramesCount = value;
            OnPropertyChanged();
        }
    }

    public string SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (_selectedMonitor == value)
            {
                return;
            }

            _selectedMonitor = value;
            OnPropertyChanged();
        }
    }

    public string SelectedOutputLanguage
    {
        get => _selectedOutputLanguage;
        set
        {
            if (_selectedOutputLanguage == value)
            {
                return;
            }

            _selectedOutputLanguage = value;
            OnPropertyChanged();
        }
    }

    public string SelectedMainPage
    {
        get => _selectedMainPage;
        set
        {
            if (_selectedMainPage == value)
            {
                return;
            }

            _selectedMainPage = value;
            OnPropertyChanged();
            // «Назад» only makes sense away from the home screen.
            _goHomeCommand?.RaiseCanExecuteChanged();
        }
    }

    public string SessionQuery
    {
        get => _sessionQuery;
        set
        {
            if (_sessionQuery == value)
            {
                return;
            }

            _sessionQuery = value;
            OnPropertyChanged();
            _completedSessionsView.Refresh();
        }
    }

    public string SelectedSessionTab
    {
        get => _selectedSessionTab;
        set
        {
            if (_selectedSessionTab == value)
            {
                return;
            }

            _selectedSessionTab = value;
            OnPropertyChanged();
        }
    }

    public SessionSummary? SelectedSession
    {
        get => _selectedSession;
        private set
        {
            if (ReferenceEquals(_selectedSession, value))
            {
                return;
            }

            _selectedSession = value;
            SelectedSessionTranscript = value == null
                ? []
                : new ObservableCollection<SessionTranscriptItem>(value.Transcript);
            OnPropertyChanged();
            OnFollowUpEmailChanged();
            _startRenameSessionCommand.RaiseCanExecuteChanged();
            _saveSessionTitleCommand.RaiseCanExecuteChanged();
        }
    }

    public string CurrentSessionTitle
    {
        get => _currentSessionTitle;
        private set
        {
            _currentSessionTitle = value;
            OnPropertyChanged();
        }
    }

    public string EditableSessionTitle
    {
        get => _editableSessionTitle;
        set
        {
            if (_editableSessionTitle == value)
            {
                return;
            }

            _editableSessionTitle = value;
            OnPropertyChanged();
        }
    }

    public bool IsEditingSessionTitle
    {
        get => _isEditingSessionTitle;
        private set
        {
            if (_isEditingSessionTitle == value)
            {
                return;
            }

            _isEditingSessionTitle = value;
            OnPropertyChanged();
            _saveSessionTitleCommand.RaiseCanExecuteChanged();
        }
    }

    public string CurrentSessionSummary
    {
        get => _currentSessionSummary;
        private set
        {
            _currentSessionSummary = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The «Статистика» tab of the selected session: tiles, the cost block and the assistant dialog.</summary>
    public SessionUsageView UsageView
    {
        get => _usageView;
        private set
        {
            _usageView = value;
            OnPropertyChanged();
        }
    }

    public string SelectedModeName
    {
        get => _selectedModeName;
        set
        {
            string normalized = string.Equals(value, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase)
                ? AppSettings.DefaultModeName
                : AppSettings.GeneralModeName;
            if (_selectedModeName == normalized)
            {
                return;
            }

            _selectedModeName = normalized;
            _settings.SelectedModeName = normalized;
            _settingsService.Save(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModesButtonText));
            OnPropertyChanged(nameof(IsDefaultModeSelected));
            OnPropertyChanged(nameof(ActiveModeRealtimePrompt));
            OnPropertyChanged(nameof(ActiveModeNotesTemplate));
            OnPropertyChanged(nameof(IsEditorModeActive));
            OnPropertyChanged(nameof(ModeActionButtonText));
            _setModeActiveCommand.RaiseCanExecuteChanged();
        }
    }

    public string ModesButtonText => $"Режимы: {SelectedModeName}";

    public string ModeEditorName
    {
        get => _modeEditorName;
        set
        {
            string normalized = string.Equals(value, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase)
                ? AppSettings.DefaultModeName
                : AppSettings.GeneralModeName;
            if (_modeEditorName == normalized)
            {
                return;
            }

            _modeEditorName = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeEditorTitle));
            OnPropertyChanged(nameof(IsEditorModeActive));
            OnPropertyChanged(nameof(ModeActionButtonText));
            OnPropertyChanged(nameof(ModeEditorRealtimePrompt));
            OnPropertyChanged(nameof(ModeEditorNotesTemplate));
            OnPropertyChanged(nameof(ModeEditorReferenceFile));
            _setModeActiveCommand.RaiseCanExecuteChanged();
        }
    }

    public string ModeEditorTitle => ModeEditorName;
    public bool IsEditorModeActive => string.Equals(ModeEditorName, SelectedModeName, StringComparison.OrdinalIgnoreCase);
    public string ModeActionButtonText => IsEditorModeActive ? "✓ Активен" : "✓ Сделать активным";

    public string ModeEditorRealtimePrompt
    {
        get => string.Equals(ModeEditorName, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase)
            ? DefaultRealtimePrompt
            : GeneralRealtimePrompt;
        set
        {
            if (string.Equals(ModeEditorName, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase))
            {
                DefaultRealtimePrompt = value;
                return;
            }

            GeneralRealtimePrompt = value;
        }
    }

    public string ModeEditorNotesTemplate
    {
        get => string.Equals(ModeEditorName, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase)
            ? DefaultNotesTemplate
            : GeneralNotesTemplate;
        set
        {
            if (string.Equals(ModeEditorName, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase))
            {
                DefaultNotesTemplate = value;
                return;
            }

            GeneralNotesTemplate = value;
        }
    }

    public string ModeEditorReferenceFile
    {
        get => string.Equals(ModeEditorName, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase)
            ? DefaultReferenceFile
            : GeneralReferenceFile;
        set
        {
            if (string.Equals(ModeEditorName, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase))
            {
                DefaultReferenceFile = value;
                return;
            }

            GeneralReferenceFile = value;
        }
    }

    public string GeneralRealtimePrompt
    {
        get => _generalRealtimePrompt;
        set
        {
            _generalRealtimePrompt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveModeRealtimePrompt));
            OnPropertyChanged(nameof(ModeEditorRealtimePrompt));
        }
    }

    public string GeneralNotesTemplate
    {
        get => _generalNotesTemplate;
        set
        {
            _generalNotesTemplate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveModeNotesTemplate));
            OnPropertyChanged(nameof(ModeEditorNotesTemplate));
        }
    }

    public string GeneralReferenceFile
    {
        get => _generalReferenceFile;
        set
        {
            if (_generalReferenceFile == value)
            {
                return;
            }

            _generalReferenceFile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeEditorReferenceFile));
        }
    }

    public string DefaultRealtimePrompt
    {
        get => _defaultRealtimePrompt;
        set
        {
            _defaultRealtimePrompt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveModeRealtimePrompt));
            OnPropertyChanged(nameof(ModeEditorRealtimePrompt));
        }
    }

    public string DefaultNotesTemplate
    {
        get => _defaultNotesTemplate;
        set
        {
            _defaultNotesTemplate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveModeNotesTemplate));
            OnPropertyChanged(nameof(ModeEditorNotesTemplate));
        }
    }

    public string DefaultReferenceFile
    {
        get => _defaultReferenceFile;
        set
        {
            if (_defaultReferenceFile == value)
            {
                return;
            }

            _defaultReferenceFile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeEditorReferenceFile));
        }
    }

    public IReadOnlyList<string> ModeOptions { get; } = [AppSettings.GeneralModeName, AppSettings.DefaultModeName];
    public bool IsDefaultModeSelected => string.Equals(SelectedModeName, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase);
    public string ActiveModeRealtimePrompt => IsDefaultModeSelected ? DefaultRealtimePrompt : GeneralRealtimePrompt;
    public string ActiveModeNotesTemplate => IsDefaultModeSelected ? DefaultNotesTemplate : GeneralNotesTemplate;

    public string CaptureStatusText
    {
        get => _captureStatusText;
        private set
        {
            _captureStatusText = value;
            OnPropertyChanged();
        }
    }

    public string PrivacyBadgeText
    {
        get => _privacyBadgeText;
        private set
        {
            _privacyBadgeText = value;
            OnPropertyChanged();
        }
    }

    public string PrivacyBadgeBrush
    {
        get => _privacyBadgeBrush;
        private set
        {
            _privacyBadgeBrush = value;
            OnPropertyChanged();
        }
    }

    public string SystemStatsText
    {
        get => _systemStatsText;
        private set
        {
            _systemStatsText = value;
            OnPropertyChanged();
        }
    }

    public string MicStatsText
    {
        get => _micStatsText;
        private set
        {
            _micStatsText = value;
            OnPropertyChanged();
        }
    }

    public string TokenUsageText
    {
        get => _tokenUsageText;
        private set
        {
            if (_tokenUsageText == value)
            {
                return;
            }

            _tokenUsageText = value;
            OnPropertyChanged();
        }
    }

    public string OverlayPrompt
    {
        get => _overlayPrompt;
        set
        {
            if (_overlayPrompt == value)
            {
                return;
            }

            _overlayPrompt = value;
            OnPropertyChanged();
        }
    }

    public string BestResponseText
    {
        get => _bestResponseText;
        private set
        {
            _bestResponseText = value;
            OnPropertyChanged();
        }
    }

    public string AlternativeResponseText
    {
        get => _alternativeResponseText;
        private set
        {
            _alternativeResponseText = value;
            OnPropertyChanged();
        }
    }

    public string FollowUpText
    {
        get => _followUpText;
        private set
        {
            _followUpText = value;
            OnPropertyChanged();
        }
    }

    public string KeyPointsText
    {
        get => _keyPointsText;
        private set
        {
            _keyPointsText = value;
            OnPropertyChanged();
        }
    }

    public string ContextSummaryText
    {
        get => _contextSummaryText;
        private set
        {
            _contextSummaryText = value;
            OnPropertyChanged();
        }
    }

    public string OverlayStateText
    {
        get => _overlayStateText;
        private set
        {
            _overlayStateText = value;
            OnPropertyChanged();
        }
    }

    public string AudioHealthMessage
    {
        get => _audioHealthMessage;
        private set
        {
            _audioHealthMessage = value;
            OnPropertyChanged();
        }
    }

    public double AudioMeterLevel
    {
        get => _audioMeterLevel;
        private set
        {
            _audioMeterLevel = value;
            OnPropertyChanged();
        }
    }

    public int OnboardingStepIndex
    {
        get => _onboardingStepIndex;
        set
        {
            if (_onboardingStepIndex == value)
            {
                return;
            }

            _onboardingStepIndex = Math.Clamp(value, 0, 4);
            OnPropertyChanged();
            OnPropertyChanged(nameof(OnboardingStepTitle));
            OnPropertyChanged(nameof(OnboardingStepText));
            _nextOnboardingCommand.RaiseCanExecuteChanged();
            _previousOnboardingCommand.RaiseCanExecuteChanged();
        }
    }

    public string OnboardingStepTitle => OnboardingStepIndex switch
    {
        0 => L["OnboardingStep0Title"],
        1 => L["OnboardingStep1Title"],
        2 => L["OnboardingStep2Title"],
        3 => L["OnboardingStep3Title"],
        _ => L["OnboardingStep4Title"]
    };

    public string OnboardingStepText => OnboardingStepIndex switch
    {
        0 => L["OnboardingStep0Text"],
        1 => L["OnboardingStep1Text"],
        2 => L["OnboardingStep2Text"],
        3 => L["OnboardingStep3Text"],
        _ => L["OnboardingStep4Text"]
    };

    // Theme colors as #RRGGBB: WPF and Avalonia bindings convert them to brushes.
    public string WindowBackgroundBrush { get; private set; } = "#000000";
    public string SurfaceBrush { get; private set; } = "#000000";
    public string SurfaceAltBrush { get; private set; } = "#000000";
    public string BorderBrush { get; private set; } = "#808080";
    public string PrimaryTextBrush { get; private set; } = "#FFFFFF";
    public string SecondaryTextBrush { get; private set; } = "#808080";
    public string AccentBrush { get; private set; } = "#00FFFF";

    public void ToggleCaptureFromHotkey()
    {
        if (_captureOrchestrator.IsRunning)
        {
            _ = StopCaptureAsync();
            AppendLog("[hotkey] Capture stop requested.");
        }
        else
        {
            StartCapture();
            AppendLog("[hotkey] Capture start requested.");
        }
    }


    public void HandleHotkey(int id)
    {
        switch (id)
        {
            case HotkeyIds.ToggleOverlay:
                ToggleOverlayRequested?.Invoke();
                AppendLog("[hotkey] Overlay toggle requested.");
                break;
            case HotkeyIds.ToggleCapture:
                ToggleCaptureFromHotkey();
                break;
            case HotkeyIds.Ask:
                _ = RunWhatShouldISayAsync();
                break;
            case HotkeyIds.ClearChat:
                ResetSessionContext();
                break;
            case HotkeyIds.MoveLeft:
                OverlayMoveRequested?.Invoke(-30, 0);
                break;
            case HotkeyIds.MoveRight:
                OverlayMoveRequested?.Invoke(30, 0);
                break;
            case HotkeyIds.MoveUp:
                OverlayMoveRequested?.Invoke(0, -30);
                break;
            case HotkeyIds.MoveDown:
                OverlayMoveRequested?.Invoke(0, 30);
                break;
            case HotkeyIds.ScrollUp:
                SelectedSessionTab = "Summary";
                break;
            case HotkeyIds.ScrollDown:
                SelectedSessionTab = "Transcript";
                break;
            case HotkeyIds.Snapshot:
                CaptureSnapshotNow();
                break;
        }
    }

    public void Shutdown()
    {
        _captureOrchestrator.Kill();
        _visualContextCapture.Stop();
        _sessionLifecycleService.Dispose();
        L.PropertyChanged -= OnLocalizationChanged;
        _logSink.Dispose();
    }

    public void CancelRenameSessionTitle()
    {
        if (SelectedSession == null)
        {
            return;
        }

        EditableSessionTitle = CurrentSessionTitle;
        IsEditingSessionTitle = false;
    }

    private void NextOnboardingStep()
    {
        OnboardingStepIndex++;
    }

    private void PreviousOnboardingStep()
    {
        OnboardingStepIndex--;
    }

    /// <summary>
    /// DesktopCapture's folder, local whisper (empty when missing) and the folder for audio pieces; shared by
    /// live capture and media transcription in the knowledge base.
    /// </summary>
    private (string CaptureDir, string WhisperBin, string WhisperModel, string SttDir) ResolveCaptureTools()
    {
        // Source checkout: apps/DesktopCapture; installed copy: DesktopCapture.exe sits in the app folder.
        string desktopCaptureProjectDir = Path.Combine(_projectRoot, "apps", "DesktopCapture");
        string desktopCaptureDir = Directory.Exists(desktopCaptureProjectDir) ? desktopCaptureProjectDir : _projectRoot;
        string whisperBinCandidate = _settings.WhisperBinPath ?? PlatformPaths.DefaultWhisperBin(_projectRoot);
        string whisperBin = File.Exists(whisperBinCandidate) ? whisperBinCandidate : string.Empty;
        string whisperModel = SpeechModelCatalog.ResolveActiveModelPath(_settings, _projectRoot) ?? string.Empty;
        string sttDir = _settings.SttDirectoryPath ?? EnvironmentDiagnosticsService.DefaultSttChunksDirectory;
        Directory.CreateDirectory(sttDir);
        return (desktopCaptureDir, whisperBin, whisperModel, sttDir);
    }

    private void StartCapture()
    {
        if (_captureOrchestrator.IsRunning)
        {
            AppendLog("[ui] Capture already running.");
            return;
        }

        Diagnostics.Refresh();
        if (!Diagnostics.IsReadyForCapture)
        {
            NotificationRequested?.Invoke(
                L["WarningDiagnosticsTitle"],
                L["WarningDiagnosticsNotReady"],
                false);
        }

        if (PlatformPermissionCheckRequested != null)
        {
            if (!PlatformPermissionCheckRequested())
            {
                AppendLog("[ui] Capture cancelled: system permissions are missing.");
                return;
            }
        }
        else if (!_permissionsConfirmed)
        {
            bool confirmed = PermissionConfirmationRequested?.Invoke() == true;
            if (!confirmed)
            {
                AppendLog("[ui] Capture cancelled: permission wizard not completed.");
                return;
            }

            _permissionsConfirmed = true;
            _settings.PermissionsConfirmed = true;
            _settingsService.Save(_settings);
            AppendLog("[ui] Permission wizard completed.");
        }

        (string desktopCaptureDir, string whisperBin, string whisperModel, string sttDir) = ResolveCaptureTools();

        bool useApiStt = UseApiStt;
        string sttApiKey = useApiStt ? ResolveSttApiKey() : string.Empty;
        if (useApiStt && string.IsNullOrWhiteSpace(sttApiKey))
        {
            useApiStt = false;
            NotificationRequested?.Invoke(L["WarningDiagnosticsTitle"], L["SttApiKeyMissing"], false);
            AppendLog("[stt] API transcription disabled for this session: API key is missing.");
        }

        try
        {
            _sessionLifecycleService.CompleteActiveIfAny(L["SessionSummaryFallback"]);
            SessionSummary session = _sessionLifecycleService.StartNew(
                BuildSessionTitle(),
                SelectedModeName,
                ActiveModeRealtimePrompt,
                ActiveModeNotesTemplate);
            _currentSessionId = session.Id;
            SelectedSession = session;
            CurrentSessionTitle = session.Title;
            EditableSessionTitle = session.Title;
            IsEditingSessionTitle = false;
            CurrentSessionSummary = L["SessionSummaryFallback"];
            VisualFramesCount = session.VisualFrames.Count;
            LastVisualCaptureText = DescribeVisualFrames(session);
            SelectedMainPage = "Session";
            SelectedSessionTab = "Transcript";
            ShowOverlayRequested?.Invoke();

            _captureOrchestrator.Start(new CaptureStartOptions
            {
                WorkingDirectory = desktopCaptureDir,
                WhisperBin = whisperBin,
                WhisperModel = whisperModel,
                SttDir = sttDir,
                ChunkSeconds = _settings.ChunkSeconds,
                MaxChunkSeconds = _settings.MaxChunkSeconds,
                Language = _settings.CaptureLanguage,
                SilenceThreshold = _settings.SttSilenceThreshold,
                MaxSttLagSeconds = _settings.SttMaxLagSeconds,
                KeepChunks = _settings.KeepAudioChunks,
                UseApiStt = useApiStt,
                SttApiUrl = string.IsNullOrWhiteSpace(SttApiBaseUrl) ? AppSettings.PolzaBaseUrl : SttApiBaseUrl.Trim(),
                SttApiModel = SttApiModel.Trim(),
                SttApiKey = sttApiKey,
                SttGlossary = SttGlossary
            });
            _lastSttCostSeen = 0;
            SttStatusText = useApiStt
                ? string.Format(L["SttStatusApi"], SttApiModel.Trim())
                : L["SttStatusLocal"];
            AppendLog("[ui] Capture started. " + SttStatusText);
            StartVisualContextIfEnabled(session.Id);
            if (!useApiStt && (string.IsNullOrWhiteSpace(whisperBin) || string.IsNullOrWhiteSpace(whisperModel)))
            {
                AppendLog("[stt] Whisper binary/model not found. Capture runs without transcription.");
                if (string.IsNullOrWhiteSpace(whisperModel))
                {
                    NotificationRequested?.Invoke(L["WarningDiagnosticsTitle"], L["SpeechModelMissingOnStart"], false);
                }
            }
            UpdateUsageText(session);
            RefreshSessionList();
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], $"{L["ErrorCaptureStart"]}\n{ex.Message}", true);
            AppendLog("[ui] Capture start failed: " + ex.Message);
        }
    }

    private async Task StopCaptureAsync()
    {
        _captureOrchestrator.Stop();
        _visualContextCapture.Stop();
        VisualContextStatusText = L["VisualStatusIdle"];
        if (!string.IsNullOrWhiteSpace(_currentSessionId))
        {
            string sessionId = _currentSessionId;
            _currentSessionId = string.Empty;
            SessionSummary? active = _sessionLifecycleService.GetById(sessionId);
            string sessionSummary = await BuildFinalSessionSummaryAsync(active);
            _sessionLifecycleService.Complete(sessionId, sessionSummary);
            _currentSessionSummary = sessionSummary;
            RefreshSessionList();
            CurrentSessionSummary = _currentSessionSummary;
        }
        SelectedSessionTab = "Summary";
        SelectedMainPage = "Home";
        HideOverlayRequested?.Invoke();
        AppendLog("[ui] Stop requested.");
    }

    private async Task<string> BuildFinalSessionSummaryAsync(SessionSummary? session)
    {
        string fallback = BuildSummaryText(session);
        if (session == null)
        {
            return fallback;
        }

        if (!CanGenerateSummaryAutomatically())
        {
            return fallback;
        }

        try
        {
            AssistResult result = await _conversationAssistService.BuildSessionSummaryAsync(
                session,
                BuildAssistOptions());
            RecordAssistUsage(session, L["ActionSummary"], result);
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                AppendLog("[assist] Post-session AI summary generated.");
                return result.Text.Trim();
            }
        }
        catch (Exception ex)
        {
            AppendLog("[assist] Post-session summary failed: " + ex.Message);
        }

        return fallback;
    }

    private bool CanGenerateSummaryAutomatically()
    {
        if ((IsApiKeyRequired() && string.IsNullOrWhiteSpace(ResolveLlmApiKey())) ||
            string.IsNullOrWhiteSpace(OpenAiModel) ||
            string.IsNullOrWhiteSpace(OpenAiBaseUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(OpenAiBaseUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;
    }

    private void RefreshDiagnostics()
    {
        Diagnostics.Refresh();
        AppendLog("[ui] Diagnostics refreshed.");
    }

    private async void ApplyQuickAction(object? actionName)
    {
        string action = actionName?.ToString() ?? "Suggest response";
        if (action.Equals("What should I say", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("Что мне сказать", StringComparison.OrdinalIgnoreCase))
        {
            await RunWhatShouldISayAsync();
            return;
        }

        OverlayPrompt = $"{L["AskPrefix"]}: {action}";
        OverlayStateText = L["StateThinking"];
        BestResponseText = L["SampleBestResponse"];
        AlternativeResponseText = L["SampleAlternativeResponse"];
        FollowUpText = L["SampleFollowUp"];
        KeyPointsText = L["SampleKeyPoints"];
        ContextSummaryText = L["SampleContextSummary"];
        OverlaySuggestionChanged?.Invoke(BestResponseText);
        if (!string.IsNullOrWhiteSpace(_currentSessionId))
        {
            _sessionLifecycleService.AddSuggestion(_currentSessionId);
        }
    }

    private void ResetSessionContext()
    {
        BestResponseText = string.Empty;
        AlternativeResponseText = string.Empty;
        FollowUpText = string.Empty;
        KeyPointsText = string.Empty;
        ContextSummaryText = string.Empty;
        OverlayPrompt = L["OverlayPrompt"];
        OverlayStateText = L["StateReady"];
        AppendLog("[context] Session context reset.");
    }

    private void SaveSettings()
    {
        if (!ValidateHotkeys(out List<string> errors))
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], string.Join(Environment.NewLine, errors), true);
            return;
        }

        _settings.UiLanguage = SelectedUiLanguage;
        _settings.ThemeMode = SelectedThemeMode;
        _settings.ToggleOverlayHotkey = NormalizeSlashHotkey(ToggleOverlayHotkeyText, "Ctrl+/");
        _settings.ToggleCaptureHotkey = NormalizeSlashHotkey(ToggleCaptureHotkeyText, "Ctrl+Shift+/");
        _settings.AskHotkey = AskHotkeyText;
        _settings.ClearChatHotkey = ClearChatHotkeyText;
        _settings.MoveLeftHotkey = MoveLeftHotkeyText;
        _settings.MoveRightHotkey = MoveRightHotkeyText;
        _settings.MoveUpHotkey = MoveUpHotkeyText;
        _settings.MoveDownHotkey = MoveDownHotkeyText;
        _settings.ScrollUpHotkey = ScrollUpHotkeyText;
        _settings.ScrollDownHotkey = ScrollDownHotkeyText;
        _settings.SnapshotHotkey = SnapshotHotkeyText;
        _settings.SmartModeEnabled = SmartModeEnabled;
        _settings.ApiPresetName = SelectedApiPreset;
        _settings.ApiProvider = ApiProvider;
        _settings.ApiKeyHeaderName = ApiKeyHeaderName;
        _settings.ApiKeyPrefix = ApiKeyPrefix;
        _settings.ChatCompletionsPath = ChatCompletionsPath;
        _settings.OpenAiApiKey = OpenAiApiKey;
        _settings.OpenAiModel = OpenAiModel;
        _settings.AnalysisModel = AnalysisModel;
        _settings.OpenAiBaseUrl = OpenAiBaseUrl;
        _settings.SttProvider = UseApiStt ? AppSettings.SttProviderApi : AppSettings.SttProviderLocal;
        _settings.SttApiPresetName = SelectedSttPreset;
        _settings.SttApiBaseUrl = string.IsNullOrWhiteSpace(SttApiBaseUrl) ? AppSettings.PolzaBaseUrl : SttApiBaseUrl.Trim();
        _settings.SttApiModel = SttApiModel;
        _settings.SttApiKey = SttApiKey;
        _settings.SttGlossary = SttGlossary;
        _settings.ShowInScreenShare = ShowInScreenShare;
        _settings.ShowInTaskbar = ShowInTaskbar;
        _settings.InvisibilityModeEnabled = InvisibilityModeEnabled;
        _settings.AutoLaunchEnabled = AutoLaunchEnabled;
        _settings.PermissionsConfirmed = _permissionsConfirmed;
        _settings.CaptureScreenContextEnabled = CaptureScreenContextEnabled;
        _settings.SelectedMonitor = SelectedMonitor;
        _settings.OutputLanguage = SelectedOutputLanguage;
        _settings.GeneralRealtimePrompt = GeneralRealtimePrompt;
        _settings.GeneralNotesTemplate = GeneralNotesTemplate;
        _settings.GeneralReferenceFile = GeneralReferenceFile;
        _settings.DefaultRealtimePrompt = DefaultRealtimePrompt;
        _settings.DefaultNotesTemplate = DefaultNotesTemplate;
        _settings.DefaultReferenceFile = DefaultReferenceFile;
        _settings.SelectedModeName = SelectedModeName;
        _settings.VideoContextEnabled = VideoContextEnabled;
        _settings.VideoContextIntervalMs = VideoContextIntervalMs;
        _settings.VisualContextMaxFrames = VisualContextMaxFrames;
        _settings.VisualContextMaxImagesPerRequest = VisualContextMaxImagesPerRequest;

        _settingsService.Save(_settings);
        ApplyTheme();
        AppendLog("[settings] Saved.");
        HotkeysChanged?.Invoke();
    }

    private void OnStatsUpdated(CaptureStats stats)
    {
        SystemStatsText = $"{L["StatsAvgShort"]} {stats.SystemAverageKb:0.0} KB/s, {L["StatsPeakShort"]} {stats.SystemPeak:0.000}";
        string tokenSuffix = string.IsNullOrWhiteSpace(TokenUsageText) ? string.Empty : $" | {TokenUsageText}";
        string sttSuffix = stats.SttLagSeconds >= 5 ? " | " + string.Format(L["OverlaySttLag"], stats.SttLagSeconds.ToString("0")) : string.Empty;
        MicStatsText = $"{L["StatsAvgShort"]} {stats.MicAverageKb:0.0} KB/s, {L["StatsPeakShort"]} {stats.MicPeak:0.000}{tokenSuffix}";
        AudioMeterLevel = Math.Clamp((stats.MicAverageKb + stats.SystemAverageKb) / 30d, 0, 1);
        AudioHealthMessage = AudioMeterLevel < 0.08 ? L["AudioHealthNoSystem"] : L["AudioHealthListening"];
        AccumulateSttCost(stats.SttCostRub);
        OverlayStatsChanged?.Invoke(
            $"{L["OverlayStatsSystem"]} {stats.SystemAverageKb:0.0} {L["UnitKbPerSecond"]} | {L["OverlayStatsMic"]} {stats.MicAverageKb:0.0} {L["UnitKbPerSecond"]}{sttSuffix}{tokenSuffix}");

        if (stats.SttLagSeconds >= 20 && DateTime.Now - _lastSttLagWarningAt > TimeSpan.FromMinutes(1))
        {
            _lastSttLagWarningAt = DateTime.Now;
            AppendLog($"[stt] Transcription lags behind: {stats.SttLagSeconds:0}s, queue={stats.SttQueueLength}.");
        }
    }

    private void OnTranscriptReceived(TranscriptLine transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript.Text) || string.IsNullOrWhiteSpace(_currentSessionId))
        {
            return;
        }

        string speakerName = ResolveSpeakerName(transcript.Source);
        var item = new SessionTranscriptItem
        {
            Timestamp = transcript.Timestamp,
            Source = transcript.Source == TranscriptSource.System ? SessionSource.System : SessionSource.Mic,
            SpeakerName = speakerName,
            RawText = transcript.RawText,
            Text = transcript.Text,
            FormattedText = transcript.FormattedText,
            SummaryReadyText = TranscriptTextPipeline.BuildSummaryReadyText(speakerName, transcript.Text)
        };

        if (_sessionLifecycleService.AddTranscriptLine(_currentSessionId, item) == null)
        {
            return;
        }

        // Append to the visible list only: no history reload, no list rebuild per line.
        if (SelectedSession?.Id == _currentSessionId)
        {
            SelectedSessionTranscript.Add(item);
            if (SelectedSessionTab == "Usage")
            {
                UpdateUsageText(SelectedSession);
            }
        }
    }

    private void OnRunningStateChanged(bool running)
    {
        IsCapturing = running;
        CaptureStatusText = running ? L["CaptureStatusCapturing"] : L["CaptureStatusIdle"];
        PrivacyBadgeText = running ? L["PrivacyOn"] : L["PrivacyOff"];
        PrivacyBadgeBrush = running ? DefaultActiveBrush : DefaultIdleBrush;
        OverlayStateText = running ? L["StateListening"] : L["StateReady"];
        OverlayCaptureStateChanged?.Invoke(running);
        _captureSnapshotCommand.RaiseCanExecuteChanged();
    }

    private void ApplyLocalization()
    {
        CaptureStatusText = IsCapturing ? L["CaptureStatusCapturing"] : L["CaptureStatusIdle"];
        PrivacyBadgeText = IsCapturing ? L["PrivacyOn"] : L["PrivacyOff"];

        if (string.IsNullOrWhiteSpace(SystemStatsText))
        {
            SystemStatsText = L["NoDataYet"];
        }

        if (string.IsNullOrWhiteSpace(MicStatsText))
        {
            MicStatsText = L["NoDataYet"];
        }

        if (string.IsNullOrWhiteSpace(OverlayPrompt))
        {
            OverlayPrompt = L["OverlayPrompt"];
        }

        if (string.IsNullOrWhiteSpace(OverlayStateText))
        {
            OverlayStateText = L["StateReady"];
        }

        if (string.IsNullOrWhiteSpace(AudioHealthMessage))
        {
            AudioHealthMessage = L["AudioHealthListening"];
        }
    }

    private void ApplyTheme()
    {
        bool dark = SelectedThemeMode != "light";
        // «Эхо»: графит и мята; светлая тема — та же гамма на светлом фоне.
        WindowBackgroundBrush = dark ? "#0E1116" : "#F3F6F5";
        SurfaceBrush = dark ? "#171B22" : "#FFFFFF";
        SurfaceAltBrush = dark ? "#1E242D" : "#E9F0EE";
        BorderBrush = dark ? "#2A313C" : "#D3DCD9";
        PrimaryTextBrush = dark ? "#E9EEF5" : "#0E1116";
        SecondaryTextBrush = dark ? "#9AA5B5" : "#4F5B67";
        AccentBrush = dark ? "#5EF0C1" : "#0B8A65";
        ApplyPalette(dark);
        OnPropertyChanged(nameof(WindowBackgroundBrush));
        OnPropertyChanged(nameof(SurfaceBrush));
        OnPropertyChanged(nameof(SurfaceAltBrush));
        OnPropertyChanged(nameof(BorderBrush));
        OnPropertyChanged(nameof(PrimaryTextBrush));
        OnPropertyChanged(nameof(SecondaryTextBrush));
        OnPropertyChanged(nameof(AccentBrush));
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]")
        {
            ApplyLocalization();
            RebuildLocalizedCollections();
            OnPropertyChanged(nameof(OnboardingStepTitle));
            OnPropertyChanged(nameof(OnboardingStepText));
        }
    }

    private void RebuildLocalizedCollections()
    {
        QuickActions.Clear();
        QuickActions.Add(L["QuickActionSummarize"]);
        QuickActions.Add(L["QuickActionSuggestResponse"]);
        QuickActions.Add(L["QuickActionObjectionHelp"]);
        QuickActions.Add(L["QuickActionWhatShouldISay"]);
        QuickActions.Add(L["QuickActionNextQuestion"]);

        SessionHistory.Clear();
        foreach (SessionSummary session in CompletedSessions.Take(3))
        {
            SessionHistory.Add($"{session.StartedAt:HH:mm} {session.Title}");
        }
        while (SessionHistory.Count < 3)
        {
            SessionHistory.Add(L[$"HistoryItem{SessionHistory.Count + 1}"]);
        }

        UiStates.Clear();
        UiStates.Add(new UiStateItem(L["StateIdle"], L["StateIdleDesc"]));
        UiStates.Add(new UiStateItem(L["StateListening"], L["StateListeningDesc"]));
        UiStates.Add(new UiStateItem(L["StateThinking"], L["StateThinkingDesc"]));
        UiStates.Add(new UiStateItem(L["StateSuggestionReady"], L["StateSuggestionReadyDesc"]));
        UiStates.Add(new UiStateItem(L["StateHidden"], L["StateHiddenDesc"]));
        UiStates.Add(new UiStateItem(L["StateNoSignal"], L["StateNoSignalDesc"]));
        UiStates.Add(new UiStateItem(L["StatePermissionsMissing"], L["StatePermissionsMissingDesc"]));
        UiStates.Add(new UiStateItem(L["StateUnsupportedSource"], L["StateUnsupportedSourceDesc"]));
        UiStates.Add(new UiStateItem(L["StateBackendDisconnected"], L["StateBackendDisconnectedDesc"]));
        UiStates.Add(new UiStateItem(L["StateTranscriptLag"], L["StateTranscriptLagDesc"]));
        UiStates.Add(new UiStateItem(L["StateCompact"], L["StateCompactDesc"]));
        UiStates.Add(new UiStateItem(L["StateExpanded"], L["StateExpandedDesc"]));
        UiStates.Add(new UiStateItem(L["StateDashboardOpen"], L["StateDashboardOpenDesc"]));
        UiStates.Add(new UiStateItem(L["StateFocusMode"], L["StateFocusModeDesc"]));
    }

    private void RefreshSpeechModels()
    {
        string? activePath = SpeechModelCatalog.ResolveActiveModelPath(_settings, _projectRoot);
        string? activeFile = activePath == null ? null : Path.GetFileName(activePath);
        foreach (SpeechModelItemViewModel item in SpeechModels)
        {
            item.IsInstalled = SpeechModelCatalog.FindInstalledPath(item.Info.FileName, _projectRoot) != null;
            item.IsDeletable = SpeechModelCatalog.IsUserDownloaded(item.Info.FileName);
            item.IsActive = string.Equals(item.Info.FileName, activeFile, StringComparison.OrdinalIgnoreCase);
        }

        OnPropertyChanged(nameof(SpeechModelStatusText));
    }

    private async void DownloadSpeechModel(object? parameter)
    {
        if (parameter is not SpeechModelItemViewModel item || _speechModelDownloadCts != null)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _speechModelDownloadCts = cts;
        DownloadSpeechModelCommand.RaiseCanExecuteChanged();
        item.ProgressPercent = 0;
        item.ProgressText = L["SpeechModelDownloadStarting"];
        item.IsDownloading = true;
        AppendLog("[stt] Downloading speech model " + item.Info.FileName + " from " + item.Info.Url);

        // Progress<T> is created on the UI thread, so its callbacks update the row there.
        var progress = new Progress<(long Received, long? Total)>(p =>
        {
            double receivedMb = p.Received / (1024.0 * 1024);
            if (p.Total is long total && total > 0)
            {
                item.ProgressPercent = p.Received * 100.0 / total;
                item.ProgressText = string.Format(L["SpeechModelDownloadProgress"], receivedMb, total / (1024.0 * 1024));
            }
            else
            {
                item.ProgressText = string.Format(L["SpeechModelDownloadProgressUnknown"], receivedMb);
            }
        });

        try
        {
            await SpeechModelCatalog.DownloadAsync(item.Info, progress, cts.Token);
            AppendLog("[stt] Speech model downloaded: " + item.Info.FileName);

            // The first downloaded model becomes active; otherwise the user picks it with "Use".
            if (SpeechModelCatalog.ResolveActiveModelPath(_settings, _projectRoot) == null ||
                SpeechModelCatalog.FindInstalledPath(_settings.SpeechModelFileName, _projectRoot) == null)
            {
                SetActiveSpeechModel(item.Info.FileName);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("[stt] Speech model download cancelled: " + item.Info.FileName);
        }
        catch (Exception ex)
        {
            AppendLog("[stt] Speech model download failed: " + ex.Message);
            NotificationRequested?.Invoke(L["ErrorTitle"], string.Format(L["SpeechModelDownloadFailed"], item.Info.Title, ex.Message), true);
        }
        finally
        {
            _speechModelDownloadCts = null;
            item.IsDownloading = false;
            item.ProgressText = string.Empty;
            DownloadSpeechModelCommand.RaiseCanExecuteChanged();
            RefreshSpeechModels();
        }
    }

    private void UseSpeechModel(object? parameter)
    {
        if (parameter is SpeechModelItemViewModel item && item.IsInstalled)
        {
            SetActiveSpeechModel(item.Info.FileName);
            RefreshSpeechModels();
        }
    }

    private void DeleteSpeechModel(object? parameter)
    {
        if (parameter is not SpeechModelItemViewModel item || !item.IsDeletable)
        {
            return;
        }

        bool confirmed = ConfirmationRequested?.Invoke(
            L["SpeechModelDeleteTitle"],
            string.Format(L["SpeechModelDeleteConfirm"], item.Info.Title)) ?? true;
        if (!confirmed)
        {
            return;
        }

        try
        {
            SpeechModelCatalog.DeleteDownloaded(item.Info.FileName);
            AppendLog("[stt] Speech model deleted: " + item.Info.FileName);
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], ex.Message, true);
        }

        RefreshSpeechModels();
    }

    /// <summary>The model choice is saved right away: it is an action, not a field waiting for "Save settings".</summary>
    private void SetActiveSpeechModel(string fileName)
    {
        _settings.SpeechModelFileName = fileName;
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            AppendLog("[settings] Could not save the speech model choice: " + ex.Message);
        }

        AppendLog("[stt] Active speech model: " + fileName);
    }

    internal void AppendLog(string line)
    {
        _logSink.Write(line);
    }

    private void RunOnUi(Action action)
    {
        _uiContext.Post(_ => action(), null);
    }

    /// <summary>Rebuilds the history list. Call on structural changes only (start/stop/rename/delete).</summary>
    private void RefreshSessionList()
    {
        IReadOnlyList<SessionSummary> sessions = _sessionLifecycleService.LoadAll();
        string? selectedId = SelectedSession?.Id;
        CompletedSessions.Clear();
        foreach (SessionSummary session in sessions.Where(s => s.Status == SessionStatus.Completed))
        {
            CompletedSessions.Add(session);
        }

        SessionSummary? active = sessions.FirstOrDefault(s => s.Status == SessionStatus.Active);
        if (active != null)
        {
            _currentSessionId = active.Id;
            SelectedModeName = string.IsNullOrWhiteSpace(active.ModeName) ? SelectedModeName : active.ModeName;
            CurrentSessionTitle = active.Title;
            EditableSessionTitle = active.Title;
            IsEditingSessionTitle = false;
            CurrentSessionSummary = BuildSummaryText(active);
            UpdateUsageText(active);
            VisualFramesCount = active.VisualFrames.Count;
            LastVisualCaptureText = DescribeVisualFrames(active);
            if (selectedId == active.Id || SelectedSession == null)
            {
                SelectedSession = active;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(CurrentSessionTitle))
            {
                CurrentSessionTitle = L["SessionTitleFallback"];
                EditableSessionTitle = CurrentSessionTitle;
                IsEditingSessionTitle = false;
            }

            if (string.IsNullOrWhiteSpace(CurrentSessionSummary))
            {
                CurrentSessionSummary = L["SessionSummaryFallback"];
            }

            if (VisualFramesCount == 0)
            {
                LastVisualCaptureText = L["VisualNoFrames"];
            }
        }

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            SessionSummary? selected = sessions.FirstOrDefault(s => s.Id == selectedId);
            if (selected != null)
            {
                SelectedSession = selected;
                if (!string.IsNullOrWhiteSpace(selected.ModeName))
                {
                    SelectedModeName = selected.ModeName;
                }
                CurrentSessionTitle = selected.Title;
                EditableSessionTitle = selected.Title;
                IsEditingSessionTitle = false;
                CurrentSessionSummary = BuildSummaryText(selected);
                UpdateUsageText(selected);
                VisualFramesCount = selected.VisualFrames.Count;
                LastVisualCaptureText = DescribeVisualFrames(selected);
            }
        }

        RefreshTopicStats();
        OnFollowUpEmailChanged();
        OnPropertyChanged(nameof(CompletedSessions));
    }

    private void CleanupVisualContextInBackground()
    {
        IReadOnlyList<SessionSummary> sessions = _sessionLifecycleService.LoadAll();
        List<SessionVisualFrame> frames = sessions.SelectMany(s => s.VisualFrames).ToList();
        List<string> sessionIds = sessions.Select(s => s.Id).ToList();
        _ = Task.Run(() => _visualContextCapture.CleanupOrphans(frames, sessionIds));
    }

    private static string BuildSessionTitle()
    {
        return $"Session {DateTime.Now:yyyy-MM-dd HH:mm}";
    }

    private string BuildSummaryText(SessionSummary? session)
    {
        if (session != null)
        {
            if (!string.IsNullOrWhiteSpace(session.SummaryText))
            {
                return ConversationAssistService.LocalizeSummaryHeadings(session.SummaryText, IsEnglishInterface);
            }

            if (session.Status == SessionStatus.Active)
            {
                return L["SessionSummaryFallback"];
            }

            if (session.Transcript.Count > 0)
            {
                IEnumerable<string> recentLines = session.Transcript
                    .TakeLast(8)
                    .Select(t => "• " + t.Text);
                return string.Join(Environment.NewLine, recentLines);
            }
        }

        if (!string.IsNullOrWhiteSpace(BestResponseText))
        {
            return BestResponseText;
        }

        return L["SampleBestResponse"];
    }

    private void UpdateUsageText(SessionSummary session)
    {
        SessionUsage usage = session.Usage;
        int manualSnapshots = session.VisualFrames.Count(IsManualFrame);
        List<UsageRow> metrics =
        [
            new(L["UsageMode"], session.ModeName, session.ModeName),
            new(L["UsageDurationMinutes"], usage.SessionDurationMinutes.ToString("0.0", CultureInfo.CurrentCulture)),
            new(L["UsageTranscriptItems"], usage.TranscriptItemsCount.ToString(CultureInfo.CurrentCulture)),
            new(L["UsageSuggestions"], usage.SuggestionsCount.ToString(CultureInfo.CurrentCulture)),
            new(L["UsageAiRequests"], usage.AiRequestsCount.ToString(CultureInfo.CurrentCulture),
                $"{L["UsageAiRequestsWithVisual"]}: {usage.AiRequestsWithVisualContextCount}"),
            new(L["UsageVisualFrames"], session.VisualFrames.Count.ToString(CultureInfo.CurrentCulture),
                $"{L["UsageManualSnapshots"]}: {manualSnapshots}")
        ];

        List<UsageRow> costRows =
        [
            new(L["UsageCostLlm"], FormatRub(usage.LlmCostRub)),
            new(L["UsageCostStt"], FormatRub(usage.SttCostRub)),
            new(L["UsageCostTotal"], FormatRub(usage.SessionCostRub)) { IsTotal = true },
            new(L["UsageAiImagesSent"], usage.AiImagesSentCount.ToString(CultureInfo.CurrentCulture))
        ];
        if (usage.LastAiRequestAt.HasValue)
        {
            costRows.Add(new(L["UsageLastAiRequest"], $"{usage.LastAiRequestAt:HH:mm:ss} · {usage.LastAiAction}"));
            costRows.Add(new(L["UsageLastTokens"], string.Format(L["UsageLastTokensValue"], usage.LastPromptTokens, usage.LastCompletionTokens, usage.LastTotalTokens)));
        }

        var dialog = new StringBuilder();
        foreach (SessionAssistMessage message in session.AssistDialog)
        {
            string role = message.Role switch
            {
                "User" => L["OverlayRoleUser"],
                "Assistant" => L["OverlayRoleAssistant"],
                _ => message.Role
            };
            dialog.Append('[')
                .Append(message.Timestamp.ToString("HH:mm:ss"))
                .Append("] ")
                .Append(message.Action)
                .Append(" | ")
                .Append(role)
                .Append(": ")
                .AppendLine(message.Text);
        }

        UsageView = new SessionUsageView
        {
            Metrics = metrics,
            CostRows = costRows,
            CostHeader = string.Format(L["UsageCostHeader"], FormatRub(usage.SessionCostRub)),
            DialogHeader = string.Format(L["UsageDialogHeader"], session.AssistDialog.Count),
            DialogText = session.AssistDialog.Count == 0 ? L["UsageAssistDialogEmpty"] : dialog.ToString().TrimEnd()
        };
    }

    private void RunAudioCaptureTest()
    {
        Diagnostics.Refresh();
        if (!Diagnostics.IsReadyForCapture)
        {
            AudioHealthMessage = L["AudioTestNotReady"];
            return;
        }

        AudioHealthMessage = IsCapturing
            ? $"{L["AudioTestRunning"]} {SystemStatsText} | {MicStatsText}"
            : L["AudioTestStartCaptureHint"];
    }

    private void OpenSession(object? parameter)
    {
        if (parameter is not SessionSummary session)
        {
            return;
        }

        if (IsSelectionMode)
        {
            // In selection mode a click on the card checks it instead of opening it.
            session.IsChecked = !session.IsChecked;
            return;
        }

        SelectedSession = session;
        if (!string.IsNullOrWhiteSpace(session.ModeName))
        {
            SelectedModeName = session.ModeName;
        }
        CurrentSessionTitle = session.Title;
        EditableSessionTitle = session.Title;
        IsEditingSessionTitle = false;
        CurrentSessionSummary = BuildSummaryText(session);
        UpdateUsageText(session);
        VisualFramesCount = session.VisualFrames.Count;
        LastVisualCaptureText = DescribeVisualFrames(session);
        SelectedMainPage = "Session";
        SelectedSessionTab = GetDefaultSessionTab(session);
    }

    private void StartRenameSessionTitle()
    {
        if (SelectedSession == null)
        {
            return;
        }

        EditableSessionTitle = CurrentSessionTitle;
        IsEditingSessionTitle = true;
    }

    private void SaveSessionTitle()
    {
        SessionSummary? session = SelectedSession;
        if (session == null)
        {
            return;
        }

        string nextTitle = EditableSessionTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(nextTitle))
        {
            nextTitle = CurrentSessionTitle;
        }

        _sessionLifecycleService.RenameSessionTitle(session.Id, nextTitle);
        IsEditingSessionTitle = false;
        RefreshSessionList();

        SessionSummary? reloaded = _sessionLifecycleService.GetById(session.Id);
        if (reloaded != null)
        {
            SelectedSession = reloaded;
            CurrentSessionTitle = reloaded.Title;
            EditableSessionTitle = reloaded.Title;
            CurrentSessionSummary = BuildSummaryText(reloaded);
            UpdateUsageText(reloaded);
        }

        AppendLog("[session] Title renamed.");
    }

    private void DeleteSession(object? parameter)
    {
        string? sessionId = parameter switch
        {
            SessionSummary s => s.Id,
            string id => id,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        string title = sessionId;
        if (parameter is SessionSummary summary)
        {
            title = summary.Title;
        }

        bool confirmed = ConfirmationRequested?.Invoke(
            L["DeleteSessionConfirmTitle"],
            string.Format(L["DeleteSessionConfirmMessage"], title)) ?? false;
        if (!confirmed)
        {
            return;
        }

        if (!RemoveSessionData(sessionId))
        {
            return;
        }

        RefreshSessionList();
        _completedSessionsView.Refresh();
        AppendLog($"[session] Deleted: {sessionId}");
    }

    /// <summary>Deletes the session file and its snapshots, and leaves the session screen if it was open.</summary>
    private bool RemoveSessionData(string sessionId)
    {
        bool removed = _sessionLifecycleService.DeleteSession(sessionId);
        if (!removed)
        {
            return false;
        }

        _ = Task.Run(() => _visualContextCapture.DeleteSessionFiles(sessionId));

        if (SelectedSession?.Id == sessionId)
        {
            SelectedSession = null;
            CurrentSessionTitle = L["SessionTitleFallback"];
            EditableSessionTitle = CurrentSessionTitle;
            IsEditingSessionTitle = false;
            CurrentSessionSummary = L["SessionSummaryFallback"];
            UsageView = SessionUsageView.Empty;
            SelectedMainPage = "Home";
        }

        if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
        {
            _currentSessionId = string.Empty;
        }

        return true;
    }

    private Task RunWhatShouldISayAsync()
    {
        string promptInput = NormalizePromptInput(OverlayPrompt);
        string prompt = string.IsNullOrWhiteSpace(promptInput) ? L["WhatShouldISayPrompt"] : promptInput;
        return RunAssistAsync(
            L["ActionAsk"],
            prompt,
            (session, options) => _conversationAssistService.BuildWhatShouldISayAsync(session, prompt, options),
            _ => L["WhatShouldISayFallback"],
            (session, reply) =>
            {
                BestResponseText = reply;
                OverlaySuggestionChanged?.Invoke(BestResponseText);
                _sessionLifecycleService.AddSuggestion(session.Id);
                SelectedMainPage = "Session";
                SelectedSessionTab = session.Status == SessionStatus.Active ? "Transcript" : "Usage";
                MainWindowAttentionRequested?.Invoke();
            },
            $"[assist] WhatShouldISay generated (smart={SmartModeEnabled}).",
            string.IsNullOrWhiteSpace(promptInput) ? L["QuickActionWhatShouldISay"] : promptInput);
    }

    private Task RegenerateSummaryAsync()
    {
        return RunAssistAsync(
            L["ActionSummary"],
            L["ActionSummaryRequest"],
            (session, options) => _conversationAssistService.BuildSessionSummaryAsync(session, options),
            BuildSummaryText,
            (session, summary) =>
            {
                CurrentSessionSummary = summary;
                OverlaySuggestionChanged?.Invoke(CurrentSessionSummary);
                _sessionLifecycleService.UpdateSummary(session.Id, CurrentSessionSummary);
            },
            $"[assist] Summary regenerated (smart={SmartModeEnabled}).");
    }

    private void CopySummaryToClipboard()
    {
        CopyToClipboard(CurrentSessionSummary ?? string.Empty);
        AppendLog("[assist] Summary copied to clipboard.");
    }

    /// <summary>
    /// Drafts the email to participants after the meeting. The draft is kept with the session and shown
    /// on the «Письмо» tab; before 1.0.1 it only went to the overlay, so the main window showed nothing.
    /// </summary>
    private async Task GenerateFollowUpEmailAsync()
    {
        if (SelectedSession == null || IsAssistBusy)
        {
            return;
        }

        SelectedSessionTab = "Email";
        IsFollowUpEmailBusy = true;
        try
        {
            await RunAssistAsync(
                L["ActionFollowUpEmail"],
                L["ActionFollowUpEmailRequest"],
                (session, options) => _conversationAssistService.BuildFollowUpEmailAsync(session, options),
                _ => L["FollowUpEmailFallback"],
                (session, emailDraft) =>
                {
                    _sessionLifecycleService.UpdateFollowUpEmail(session.Id, emailDraft);
                    _sessionLifecycleService.AddSuggestion(session.Id);
                    if (SelectedSession?.Id == session.Id)
                    {
                        OnFollowUpEmailChanged();
                        SelectedSessionTab = "Email";
                    }
                },
                "[assist] Follow-up email generated.");
        }
        finally
        {
            IsFollowUpEmailBusy = false;
        }
    }

    private Task GenerateFollowUpQuestionAsync()
    {
        string promptInput = NormalizePromptInput(OverlayPrompt);
        string prompt = string.IsNullOrWhiteSpace(promptInput)
            ? L["ActionFollowUpQuestionsRequest"]
            : string.Format(L["ActionFollowUpQuestionsRequestWithPrompt"], promptInput);
        return RunAssistAsync(
            L["ActionFollowUpQuestions"],
            prompt,
            (session, options) => _conversationAssistService.BuildFollowUpQuestionsAsync(session, prompt, options),
            _ => L["SampleFollowUp"],
            (session, questions) =>
            {
                FollowUpText = questions;
                OverlaySuggestionChanged?.Invoke(FollowUpText);
                _sessionLifecycleService.AddSuggestion(session.Id);
            },
            "[assist] Follow-up question generated.");
    }

    private Task GenerateRecapAsync()
    {
        return RunAssistAsync(
            L["ActionRecap"],
            L["ActionRecapRequest"],
            (session, options) => _conversationAssistService.BuildRecapAsync(session, options),
            _ => L["SessionSummaryFallback"],
            (session, recap) =>
            {
                ContextSummaryText = recap;
                BestResponseText = ContextSummaryText;
                OverlaySuggestionChanged?.Invoke(ContextSummaryText);
                _sessionLifecycleService.AddSuggestion(session.Id);
            },
            "[assist] Recap generated.");
    }

    private Task SuggestReplyAsync()
    {
        return RunAssistAsync(
            L["ActionWhatToSay"],
            L["ActionWhatToSayRequest"],
            (session, options) => _conversationAssistService.BuildReplySuggestionAsync(session, options),
            _ => L["WhatShouldISayFallback"],
            (session, reply) =>
            {
                BestResponseText = reply;
                OverlaySuggestionChanged?.Invoke(BestResponseText);
                _sessionLifecycleService.AddSuggestion(session.Id);
            },
            "[assist] Reply suggestion generated.");
    }

    private Task SuggestNextQuestionAsync()
    {
        return RunAssistAsync(
            L["ActionWhatToAsk"],
            L["ActionWhatToAskRequest"],
            (session, options) => _conversationAssistService.BuildNextQuestionSuggestionAsync(session, options),
            _ => L["SampleFollowUp"],
            (session, nextQuestion) =>
            {
                FollowUpText = nextQuestion;
                OverlaySuggestionChanged?.Invoke(FollowUpText);
                _sessionLifecycleService.AddSuggestion(session.Id);
            },
            "[assist] Next question suggestion generated.");
    }

    /// <summary>
    /// Shared pipeline for every assist action: guards, dialog history, LLM call, usage accounting, errors.
    /// </summary>
    private async Task RunAssistAsync(
        string action,
        string userDialogText,
        Func<SessionSummary, AssistOptions, Task<AssistResult>> request,
        Func<SessionSummary, string> fallback,
        Action<SessionSummary, string> applyResult,
        string successLogLine,
        string? overlayQuestion = null)
    {
        SessionSummary? session = SelectedSession;
        if (session == null)
        {
            return;
        }

        if (IsAssistBusy)
        {
            AppendLog("[assist] Request ignored: assist is already running.");
            return;
        }

        if (!TryValidateAssistSettings(out string settingsError))
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], settingsError, true);
            return;
        }

        IsAssistBusy = true;
        OverlayStateText = L["StateThinking"];
        if (!string.IsNullOrWhiteSpace(overlayQuestion))
        {
            // The question asked from the main screen also goes into the overlay dialog, above its answer.
            OverlayQuestionAsked?.Invoke(overlayQuestion);
        }

        try
        {
            AppendAssistDialog(session.Id, action, "User", userDialogText);
            AssistResult result = await request(session, BuildAssistOptions());
            RecordAssistUsage(session, action, result);

            string text = string.IsNullOrWhiteSpace(result.Text) ? fallback(session) : result.Text.Trim();
            applyResult(session, text);
            AppendAssistDialog(session.Id, action, "Assistant", text);
            if (SelectedSession?.Id == session.Id)
            {
                UpdateUsageText(session);
            }

            OverlayStateText = L["StateSuggestionReady"];
            AppendLog(successLogLine);
        }
        catch (Exception ex)
        {
            OverlayStateText = L["StateReady"];
            OverlayAnswerFailed?.Invoke();
            NotificationRequested?.Invoke(L["ErrorTitle"], $"{L["OpenAiRequestFailed"]}\n{ex.Message}", true);
            AppendLog($"[assist] {action} failed: " + ex.Message);
        }
        finally
        {
            IsAssistBusy = false;
        }
    }

    private void OpenActiveSession()
    {
        if (SelectedSession != null)
        {
            SelectedMainPage = "Session";
            SelectedSessionTab = GetDefaultSessionTab(SelectedSession);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_currentSessionId))
        {
            SessionSummary? active = _sessionLifecycleService.GetById(_currentSessionId);
            if (active != null)
            {
                SelectedSession = active;
                CurrentSessionTitle = active.Title;
                EditableSessionTitle = active.Title;
                IsEditingSessionTitle = false;
                CurrentSessionSummary = BuildSummaryText(active);
                UpdateUsageText(active);
                SelectedMainPage = "Session";
                SelectedSessionTab = GetDefaultSessionTab(active);
                return;
            }
        }

        SelectedMainPage = "Home";
    }

    private static string GetDefaultSessionTab(SessionSummary? session)
    {
        return session?.Status == SessionStatus.Active ? "Transcript" : "Summary";
    }

    private string ResolveSpeakerName(TranscriptSource source)
    {
        string configured = source == TranscriptSource.Mic
            ? _settings.MicParticipantName
            : _settings.SystemParticipantName;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return source == TranscriptSource.Mic ? "Speaker 1" : "Speaker 2";
    }

    private bool TryValidateAssistSettings(out string errorMessage)
    {
        if (IsApiKeyRequired() && string.IsNullOrWhiteSpace(ResolveLlmApiKey()))
        {
            errorMessage = L["OpenAiKeyMissing"];
            return false;
        }

        if (string.IsNullOrWhiteSpace(OpenAiModel))
        {
            errorMessage = L["OpenAiModelMissing"];
            return false;
        }

        if (string.IsNullOrWhiteSpace(OpenAiBaseUrl))
        {
            errorMessage = L["OpenAiBaseUrlMissing"];
            return false;
        }

        if (string.IsNullOrWhiteSpace(ChatCompletionsPath))
        {
            errorMessage = "Endpoint path is missing. Example: /chat/completions";
            return false;
        }

        if (!Uri.TryCreate(OpenAiBaseUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            errorMessage = L["OpenAiBaseUrlInvalid"];
            return false;
        }

        if (string.IsNullOrWhiteSpace(ApiKeyHeaderName))
        {
            errorMessage = "API key header name is missing. Example: Authorization";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private void ApplyAssistProviderSettings()
    {
        NormalizeModelForSelectedPreset();
        _settings.SelectedModeName = SelectedModeName;
        _settings.GeneralRealtimePrompt = GeneralRealtimePrompt;
        _settings.DefaultRealtimePrompt = DefaultRealtimePrompt;
        _settings.GeneralNotesTemplate = GeneralNotesTemplate;
        _settings.DefaultNotesTemplate = DefaultNotesTemplate;
        _settings.CaptureScreenContextEnabled = CaptureScreenContextEnabled;
        _settings.VideoContextEnabled = VideoContextEnabled;
        _settings.VideoContextIntervalMs = VideoContextIntervalMs;
        _settings.VisualContextMaxFrames = VisualContextMaxFrames;
        _settings.VisualContextMaxImagesPerRequest = VisualContextMaxImagesPerRequest;
        _settings.ApiPresetName = SelectedApiPreset;
        _settings.ApiProvider = ApiProvider;
        _settings.ApiKeyHeaderName = ApiKeyHeaderName;
        _settings.ApiKeyPrefix = ApiKeyPrefix;
        _settings.ChatCompletionsPath = ChatCompletionsPath;
        _settings.OpenAiApiKey = OpenAiApiKey;
        _settings.OpenAiModel = OpenAiModel;
        _settings.AnalysisModel = AnalysisModel;
        _settings.OpenAiBaseUrl = OpenAiBaseUrl;
    }

    private AssistOptions BuildAssistOptions()
    {
        ApplyAssistProviderSettings();
        return new AssistOptions
        {
            Endpoint = new LlmEndpoint
            {
                BaseUrl = OpenAiBaseUrl.Trim(),
                ApiKey = ResolveLlmApiKey(),
                EndpointPath = ChatCompletionsPath,
                ApiKeyHeaderName = ApiKeyHeaderName,
                ApiKeyPrefix = ApiKeyPrefix
            },
            FastModel = OpenAiModel.Trim(),
            AnalysisModel = AnalysisModel?.Trim() ?? string.Empty,
            SmartModeEnabled = SmartModeEnabled,
            Settings = _settings
        };
    }

    /// <summary>Key from settings, otherwise POLZA_API_KEY / secrets.txt.</summary>
    private string ResolveLlmApiKey()
    {
        return string.IsNullOrWhiteSpace(OpenAiApiKey) ? _secrets.GetLlmApiKey() : OpenAiApiKey.Trim();
    }

    /// <summary>Dedicated STT key, otherwise the LLM key when both use the same host (e.g. Polza.ai).</summary>
    private string ResolveSttApiKey()
    {
        if (!string.IsNullOrWhiteSpace(SttApiKey))
        {
            return SttApiKey.Trim();
        }

        string secretKey = _secrets.GetSttApiKey();
        if (!string.IsNullOrWhiteSpace(secretKey))
        {
            return secretKey;
        }

        string sttBaseUrl = string.IsNullOrWhiteSpace(SttApiBaseUrl) ? AppSettings.PolzaBaseUrl : SttApiBaseUrl.Trim();
        bool sameHost = Uri.TryCreate(sttBaseUrl, UriKind.Absolute, out Uri? sttUri) &&
            Uri.TryCreate(OpenAiBaseUrl, UriKind.Absolute, out Uri? llmUri) &&
            string.Equals(sttUri.Host, llmUri.Host, StringComparison.OrdinalIgnoreCase);
        return sameHost ? ResolveLlmApiKey() : string.Empty;
    }

    private void NotifyApiPresetDependentProperties()
    {
        OnPropertyChanged(nameof(ApiKeyFieldVisibility));
        OnPropertyChanged(nameof(ProviderFieldVisibility));
        OnPropertyChanged(nameof(BaseUrlFieldVisibility));
        OnPropertyChanged(nameof(EndpointPathFieldVisibility));
        OnPropertyChanged(nameof(ApiKeyHeaderFieldVisibility));
        OnPropertyChanged(nameof(ApiKeyPrefixFieldVisibility));
    }

    private bool IsCustomPreset()
    {
        return string.Equals(SelectedApiPreset, "Custom", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsApiKeyRequired()
    {
        return !IsLocalPreset(SelectedApiPreset);
    }

    private static bool IsLocalPreset(string preset)
    {
        return string.Equals(preset, "LM Studio (local)", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preset, "vLLM (local)", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preset, "Ollama (OpenAI-compatible)", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyApiPreset(string preset)
    {
        if (string.IsNullOrWhiteSpace(preset) ||
            string.Equals(preset, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApiKeyHeaderName = "Authorization";
        ApiKeyPrefix = "Bearer";
        ChatCompletionsPath = "/chat/completions";

        AnalysisModel = string.Empty;
        switch (preset)
        {
            case AppSettings.PolzaPresetName:
                ApiProvider = AppSettings.PolzaPresetName;
                OpenAiBaseUrl = AppSettings.PolzaBaseUrl;
                OpenAiModel = AppSettings.PolzaFastModel;
                AnalysisModel = AppSettings.PolzaAnalysisModel;
                break;
            case "OpenAI":
                ApiProvider = "OpenAI Compatible";
                OpenAiBaseUrl = "https://api.openai.com/v1";
                OpenAiModel = "gpt-4.1-mini";
                break;
            case "OpenRouter":
                ApiProvider = "OpenRouter";
                OpenAiBaseUrl = "https://openrouter.ai/api/v1";
                OpenAiModel = "openai/gpt-4.1-mini";
                break;
            case "Groq":
                ApiProvider = "Groq";
                OpenAiBaseUrl = "https://api.groq.com/openai/v1";
                OpenAiModel = "llama-3.3-70b-versatile";
                break;
            case "Together":
                ApiProvider = "Together";
                OpenAiBaseUrl = "https://api.together.xyz/v1";
                OpenAiModel = "meta-llama/Llama-3.3-70B-Instruct-Turbo";
                break;
            case "LM Studio (local)":
                ApiProvider = "LM Studio";
                OpenAiBaseUrl = "http://localhost:1234/v1";
                OpenAiModel = "local-model";
                break;
            case "vLLM (local)":
                ApiProvider = "vLLM";
                OpenAiBaseUrl = "http://localhost:8000/v1";
                OpenAiModel = "local-model";
                break;
            case "Ollama (OpenAI-compatible)":
                ApiProvider = "Ollama";
                OpenAiBaseUrl = "http://localhost:11434/v1";
                OpenAiModel = "llama3.1:8b";
                break;
        }
    }

    private void NormalizeModelForSelectedPreset()
    {
        if (string.IsNullOrWhiteSpace(OpenAiModel))
        {
            return;
        }

        string model = OpenAiModel.Trim();
        if (string.Equals(SelectedApiPreset, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            int slashIndex = model.LastIndexOf('/');
            if (slashIndex >= 0 && slashIndex < model.Length - 1)
            {
                OpenAiModel = model[(slashIndex + 1)..];
            }
        }
    }

    private static string InferApiPresetFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "Custom";
        }

        string normalized = url.ToLowerInvariant();
        if (normalized.Contains("polza.ai"))
        {
            return AppSettings.PolzaPresetName;
        }

        if (normalized.Contains("api.openai.com"))
        {
            return "OpenAI";
        }

        if (normalized.Contains("openrouter.ai"))
        {
            return "OpenRouter";
        }

        if (normalized.Contains("api.groq.com"))
        {
            return "Groq";
        }

        if (normalized.Contains("api.together.xyz"))
        {
            return "Together";
        }

        if (normalized.Contains("localhost:1234"))
        {
            return "LM Studio (local)";
        }

        if (normalized.Contains("localhost:8000"))
        {
            return "vLLM (local)";
        }

        if (normalized.Contains("localhost:11434"))
        {
            return "Ollama (OpenAI-compatible)";
        }

        return "Custom";
    }

    private void EnsureModeExamples()
    {
        if (string.IsNullOrWhiteSpace(GeneralRealtimePrompt))
        {
            GeneralRealtimePrompt = "Пример: отвечай кратко и по делу, предлагай конкретные шаги и уточняй риски.";
        }

        if (string.IsNullOrWhiteSpace(DefaultRealtimePrompt))
        {
            DefaultRealtimePrompt = "Пример: сначала уточни задачу, затем предложи 2-3 варианта решения и лучший из них.";
        }

        if (string.IsNullOrWhiteSpace(GeneralNotesTemplate))
        {
            GeneralNotesTemplate = "Пример заметок:\n- Цель\n- Ключевые решения\n- Следующие шаги";
        }

        if (string.IsNullOrWhiteSpace(DefaultNotesTemplate))
        {
            DefaultNotesTemplate = "Пример заметок:\n- Контекст\n- Что сделали\n- Что проверить после";
        }

        if (string.IsNullOrWhiteSpace(GeneralReferenceFile))
        {
            GeneralReferenceFile = "Файл не выбран.";
        }

        if (string.IsNullOrWhiteSpace(DefaultReferenceFile))
        {
            DefaultReferenceFile = "Файл не выбран.";
        }
    }

    private void SetModeActive()
    {
        SelectedModeName = ModeEditorName;
        AppendLog($"[modes] Active mode changed to {SelectedModeName}.");
    }

    private async void UploadModeReferenceFile()
    {
        IReadOnlyList<string> files = await PickFilesAsync("Выберите файл контекста для режима", "Все файлы", "*.*", false);
        if (files.Count == 0)
        {
            return;
        }

        ModeEditorReferenceFile = files[0];
        AppendLog($"[modes] Reference file selected for {ModeEditorName}: {files[0]}");
    }

    private void RequestNewMode()
    {
        NotificationRequested?.Invoke("Режимы", "В этой версии доступны два встроенных режима: General и Default.", false);
    }

    private void AppendAssistDialog(string sessionId, string action, string role, string text)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _sessionLifecycleService.AddAssistMessage(
            sessionId,
            new SessionAssistMessage
            {
                Timestamp = DateTime.Now,
                Action = action,
                Role = role,
                Text = text.Trim()
            });
    }

    private void StartVisualContextIfEnabled(string sessionId)
    {
        if (!CaptureScreenContextEnabled)
        {
            VisualContextStatusText = L["VisualStatusDisabled"];
            return;
        }

        if (VideoContextEnabled)
        {
            _visualContextCapture.Start(sessionId, VideoContextIntervalMs);
            VisualContextStatusText = string.Format(L["VisualStatusAuto"], VideoContextIntervalMs);
        }
        else
        {
            _visualContextCapture.Stop();
            VisualContextStatusText = L["VisualStatusManualOnly"];
        }
    }

    private void OnVisualFrameCaptured(SessionVisualFrame frame)
    {
        if (string.IsNullOrWhiteSpace(_currentSessionId))
        {
            _visualContextCapture.DeleteFrameFiles([frame]);
            return;
        }

        IReadOnlyList<SessionVisualFrame> removed = _sessionLifecycleService.AddVisualFrame(_currentSessionId, frame, VisualContextMaxFrames);
        if (removed.Count > 0)
        {
            _ = Task.Run(() => _visualContextCapture.DeleteFrameFiles(removed));
        }

        SessionSummary? updated = _sessionLifecycleService.GetById(_currentSessionId);
        if (updated == null)
        {
            return;
        }

        VisualFramesCount = updated.VisualFrames.Count;
        LastVisualCaptureText = string.Format(
            L["VisualLastFrame"],
            frame.Timestamp.ToString("HH:mm:ss"),
            IsManualFrame(frame) ? L["VisualSourceManual"] : L["VisualSourceAuto"],
            updated.VisualFrames.Count);
        if (SelectedSession?.Id == updated.Id && SelectedSessionTab == "Usage")
        {
            UpdateUsageText(updated);
        }
    }

    private void CaptureSnapshotNow()
    {
        if (!IsCapturing || !CaptureScreenContextEnabled || string.IsNullOrWhiteSpace(_currentSessionId))
        {
            return;
        }

        if (_visualContextCapture.CaptureSnapshot("manual", true, out SessionVisualFrame? frame) && frame != null)
        {
            OnVisualFrameCaptured(frame);
            OverlayNoticeRequested?.Invoke(L["SnapshotTakenNotice"]);
            AppendLog("[visual] Manual snapshot captured.");
            return;
        }

        OverlayNoticeRequested?.Invoke(L["SnapshotSkippedNotice"]);
        AppendLog("[visual] Manual snapshot skipped.");
    }

    private void RecordAssistUsage(SessionSummary session, string action, AssistResult result)
    {
        AssistTokenUsage? usage = result.Usage;
        _sessionLifecycleService.UpdateAssistUsage(session.Id, action, result.AttachedImages, usage);
        if (SelectedSession?.Id == session.Id)
        {
            UpdateUsageText(session);
        }

        if (usage != null)
        {
            // The cost lives in the session's «Статистика» tab, not in the overlay footer.
            TokenUsageText = string.Format(L["OverlayTokens"], usage.TotalTokens);
            if (!IsCapturing)
            {
                // Without capture there are no stats ticks to refresh the overlay footer.
                OverlayStatsChanged?.Invoke(TokenUsageText);
            }
            return;
        }

        if (result.AttachedImages > 0)
        {
            TokenUsageText = string.Format(L["OverlayNoUsage"], result.AttachedImages);
        }
    }

    /// <summary>DesktopCapture reports the cumulative API transcription cost; add only the increase.</summary>
    private void AccumulateSttCost(double cumulativeCostRub)
    {
        if (string.IsNullOrWhiteSpace(_currentSessionId) || cumulativeCostRub <= _lastSttCostSeen)
        {
            return;
        }

        double delta = cumulativeCostRub - _lastSttCostSeen;
        _lastSttCostSeen = cumulativeCostRub;
        _sessionLifecycleService.AddSttCost(_currentSessionId, delta);
        if (SelectedSession?.Id == _currentSessionId)
        {
            UpdateUsageText(SelectedSession);
        }
    }

    private static string FormatRub(double value)
    {
        return value.ToString(value < 1 ? "0.###" : "0.##", CultureInfo.CurrentCulture) + " ₽";
    }

    private static bool IsManualFrame(SessionVisualFrame frame)
    {
        return string.Equals(frame.Source, "manual", StringComparison.OrdinalIgnoreCase);
    }

    private string DescribeVisualFrames(SessionSummary session)
    {
        return session.VisualFrames.Count == 0
            ? L["VisualNoFrames"]
            : string.Format(L["VisualFramesCount"], session.VisualFrames.Count);
    }

    /// <summary>Which source ResolveSttApiKey would use; suffix of the SttKeySource* resource keys.</summary>
    private string DescribeSttKeySource()
    {
        if (!string.IsNullOrWhiteSpace(SttApiKey))
        {
            return "Field";
        }

        if (!string.IsNullOrWhiteSpace(_secrets.GetSttApiKey()))
        {
            return "Secrets";
        }

        if (string.IsNullOrWhiteSpace(ResolveSttApiKey()))
        {
            return "Missing";
        }

        return string.IsNullOrWhiteSpace(OpenAiApiKey) ? "LlmSecrets" : "LlmField";
    }

    private string NormalizePromptInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.Trim();
        if (string.Equals(normalized, L["OverlayPrompt"], StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return normalized;
    }

    private bool FilterSessionItem(object item)
    {
        if (item is not SessionSummary session)
        {
            return false;
        }

        if (!FilterByTopic(session))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SessionQuery))
        {
            return true;
        }

        string query = SessionQuery.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        string searchBlob = BuildSessionSearchBlob(session);
        return searchBlob.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSessionSearchBlob(SessionSummary session)
    {
        var builder = new StringBuilder();
        builder.Append(session.Title).Append(' ');
        builder.Append(session.ModeName).Append(' ');
        builder.Append(session.SummaryText).Append(' ');
        foreach (SessionTranscriptItem line in session.Transcript)
        {
            builder.Append(line.Text).Append(' ');
            builder.Append(line.RawText).Append(' ');
            builder.Append(line.FormattedText).Append(' ');
            builder.Append(line.SummaryReadyText).Append(' ');
        }

        foreach (SessionAssistMessage message in session.AssistDialog)
        {
            builder.Append(message.Action).Append(' ');
            builder.Append(message.Role).Append(' ');
            builder.Append(message.Text).Append(' ');
        }

        foreach (SessionVisualFrame frame in session.VisualFrames)
        {
            builder.Append(frame.WindowTitle).Append(' ');
            builder.Append(frame.OcrText).Append(' ');
            builder.Append(frame.ChangeReason).Append(' ');
        }

        builder.Append(session.Usage.TranscriptItemsCount).Append(' ');
        builder.Append(session.Usage.SuggestionsCount).Append(' ');
        builder.Append(session.Usage.SessionDurationMinutes.ToString("0.0"));
        return builder.ToString();
    }

    private static string NormalizeSlashHotkey(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return text.Replace("\\", "/", StringComparison.Ordinal);
    }


}

public class UiStateItem
{
    public UiStateItem(string stateName, string description)
    {
        StateName = stateName;
        Description = description;
    }

    public string StateName { get; }
    public string Description { get; }
}

/// <summary>One tile or line of the «Статистика» tab.</summary>
public sealed record UsageRow(string Label, string Value, string? Hint = null)
{
    public bool IsTotal { get; init; }
}

/// <summary>What the «Статистика» tab shows for one session.</summary>
public sealed class SessionUsageView
{
    public static readonly SessionUsageView Empty = new();

    public IReadOnlyList<UsageRow> Metrics { get; init; } = [];
    public IReadOnlyList<UsageRow> CostRows { get; init; } = [];
    public string CostHeader { get; init; } = string.Empty;
    public string DialogHeader { get; init; } = string.Empty;
    public string DialogText { get; init; } = string.Empty;
    public bool HasData => Metrics.Count > 0;
}
