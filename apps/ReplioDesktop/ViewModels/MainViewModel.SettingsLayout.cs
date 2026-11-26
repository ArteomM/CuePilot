using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// Settings in seven sections with a menu on the left (Arteom 2026-11-25: «сгруппировать, упростить, сделать
/// дружелюбнее», each plan sees only what it can use, no «API» or «whisper.cpp» in the texts):
/// Аккаунт · Встречи · Речь · ИИ-помощник · Окна и вид · Горячие клавиши · Данные и обновления.
/// Where speech is recognised depends on the plan:
/// • Бесплатный, Старт: in the Replio cloud, nothing to choose;
/// • Про, Команда: «Качество распознавания» (Стандарт or Точная);
/// • Локальный: only on this computer, with the model list (the gateway offers this plan no cloud speech);
/// • Приватный: the company's own server, set up by its administrator (employees see no keys or addresses);
/// • developer mode: «На этом компьютере» or «Через свой сервис» with the service fields.
/// Changes are saved by themselves a moment after the last edit; a hotkey that cannot be read is not applied and
/// its error is shown under the hotkeys until it is fixed.
/// </summary>
public partial class MainViewModel
{
    public const string SettingsAccount = "account";
    public const string SettingsMeetings = "meetings";
    public const string SettingsSpeech = "speech";
    public const string SettingsAi = "ai";
    public const string SettingsWindows = "windows";
    public const string SettingsHotkeys = "hotkeys";
    public const string SettingsData = "data";

    /// <summary>Properties that end up in settings.json: a change to any of them saves Settings shortly after.</summary>
    private static readonly HashSet<string> AutoSavedSettings =
    [
        nameof(ToggleOverlayHotkeyText), nameof(ToggleCaptureHotkeyText), nameof(AskHotkeyText), nameof(ClearChatHotkeyText),
        nameof(MoveLeftHotkeyText), nameof(MoveRightHotkeyText), nameof(MoveUpHotkeyText), nameof(MoveDownHotkeyText),
        nameof(ScrollUpHotkeyText), nameof(ScrollDownHotkeyText), nameof(SnapshotHotkeyText), nameof(SmartModeEnabled),
        nameof(SelectedApiPreset), nameof(ApiProvider), nameof(ApiKeyHeaderName), nameof(ApiKeyPrefix), nameof(ChatCompletionsPath),
        nameof(OpenAiApiKey), nameof(OpenAiModel), nameof(AnalysisModel), nameof(OpenAiBaseUrl), nameof(UseApiStt),
        nameof(SelectedSttPreset), nameof(SttApiBaseUrl), nameof(SttApiModel), nameof(SttApiKey), nameof(SttGlossary),
        nameof(ShowInScreenShare), nameof(ShowInTaskbar), nameof(InvisibilityModeEnabled), nameof(AutoLaunchEnabled),
        nameof(CaptureScreenContextEnabled), nameof(SelectedMonitor), nameof(SelectedOutputLanguage), nameof(VideoContextEnabled),
        nameof(VideoContextIntervalMs), nameof(VisualContextMaxFrames), nameof(VisualContextMaxImagesPerRequest), nameof(SelectedUiLanguage)
    ];

    private string _settingsSection = SettingsAccount;
    private UiTimer? _settingsAutoSaveTimer;
    private bool _settingsAutoSaveReady;
    private string _settingsSaveStatus = string.Empty;
    private string _hotkeyErrorText = string.Empty;
    private bool _settingsDirty;

    public RelayCommand ResetHotkeysCommand { get; private set; } = null!;

    private void InitializeSettingsLayout()
    {
        ResetHotkeysCommand = new RelayCommand(ResetHotkeys);
        ToggleScreenShareCommand = new RelayCommand(() =>
        {
            HideInScreenShare = !HideInScreenShare;
            ShowFeedback(L[HideInScreenShare ? "ScreenShareHiddenFeedback" : "ScreenShareShownFeedback"]);
        });
        _settingsAutoSaveTimer = new UiTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _settingsAutoSaveTimer.Tick += (_, _) =>
        {
            _settingsAutoSaveTimer.Stop();
            SaveSettingsCore(manual: false);
        };
        PropertyChanged += (_, e) =>
        {
            if (_settingsAutoSaveReady && e.PropertyName != null && AutoSavedSettings.Contains(e.PropertyName))
            {
                _settingsDirty = true;
                _settingsAutoSaveTimer.Stop();
                _settingsAutoSaveTimer.Start();
            }
        };
        _settingsAutoSaveReady = true;
    }

    /// <summary>Writes a pending change at once (app exit within the short delay).</summary>
    public void FlushSettingsAutoSave()
    {
        if (_settingsAutoSaveTimer == null || !_settingsAutoSaveReady)
        {
            return;
        }

        _settingsAutoSaveTimer.Stop();
        if (_settingsDirty)
        {
            SaveSettingsCore(manual: false);
        }
    }

    private string SettingsSavedText => string.Format(L["SettingsSavedAt"], DateTime.Now.ToString("HH:mm"));

    // ---------- the menu ----------

    public string SettingsSection
    {
        get => _settingsSection;
        set
        {
            if (_settingsSection == value)
            {
                return;
            }

            _settingsSection = value;
            OnPropertyChanged();
            OnSettingsSectionChangedForAudio();
            foreach (string name in new[]
            {
                nameof(IsSettingsAccount), nameof(IsSettingsMeetings), nameof(IsSettingsSpeech), nameof(IsSettingsAi),
                nameof(IsSettingsWindows), nameof(IsSettingsHotkeys), nameof(IsSettingsData), nameof(SettingsSectionTitle),
                nameof(SettingsSectionSubtitle)
            })
            {
                OnPropertyChanged(name);
            }
        }
    }

    public bool IsSettingsAccount { get => SettingsSection == SettingsAccount; set { if (value) SettingsSection = SettingsAccount; } }
    public bool IsSettingsMeetings { get => SettingsSection == SettingsMeetings; set { if (value) SettingsSection = SettingsMeetings; } }
    public bool IsSettingsSpeech { get => SettingsSection == SettingsSpeech; set { if (value) SettingsSection = SettingsSpeech; } }
    public bool IsSettingsAi { get => SettingsSection == SettingsAi; set { if (value) SettingsSection = SettingsAi; } }
    public bool IsSettingsWindows { get => SettingsSection == SettingsWindows; set { if (value) SettingsSection = SettingsWindows; } }
    public bool IsSettingsHotkeys { get => SettingsSection == SettingsHotkeys; set { if (value) SettingsSection = SettingsHotkeys; } }
    public bool IsSettingsData { get => SettingsSection == SettingsData; set { if (value) SettingsSection = SettingsData; } }

    public string SettingsSectionTitle => L["SettingsNav_" + SettingsSection];

    public string SettingsSectionSubtitle => L["SettingsNavHint_" + SettingsSection];

    /// <summary>«Изменения сохраняются сами», then «Сохранено в 19:42».</summary>
    public string SettingsSaveStatus
    {
        get => _settingsSaveStatus.Length > 0 ? _settingsSaveStatus : L["SettingsAutoSaveHint"];
        private set
        {
            if (_settingsSaveStatus == value)
            {
                return;
            }

            _settingsSaveStatus = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Why a hotkey was not applied; empty while all of them are fine.</summary>
    public string HotkeyErrorText
    {
        get => _hotkeyErrorText;
        private set
        {
            if (_hotkeyErrorText == value)
            {
                return;
            }

            _hotkeyErrorText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHotkeyError));
        }
    }

    public bool HasHotkeyError => _hotkeyErrorText.Length > 0;

    // ---------- speech by plan ----------

    /// <summary>«Локальный»: local recognition without own providers.</summary>
    public bool IsLocalSpeechPlan => CanUseLocalStt && !CanUseOwnProviders;

    /// <summary>Про and Команда: more than one cloud model to choose from.</summary>
    public bool ShowSttQualityChoice => IsManagedAi && !CanUseLocalStt && ReplioSttModels.Count > 1;

    /// <summary>Бесплатный, Старт (and signed out): cloud recognition with nothing to choose.</summary>
    public bool ShowSttCloudInfo => IsManagedAi && !CanUseLocalStt && !ShowSttQualityChoice;

    public string SttCloudInfoTitle => string.Format(L["SttWhereCloud"], SelectedReplioSttModel?.Title ?? L["SttStandardName"]);

    public string SttCloudInfoHint => IsSignedIn ? L["SttCloudUpgradeHint"] : L["SttSignInHint"];

    /// <summary>Developer mode: on this computer or through an own service.</summary>
    public bool ShowSttWhereChoice => IsDeveloperMode;

    public bool IsSttOnComputer
    {
        get => !UseApiStt;
        set
        {
            if (value)
            {
                UseApiStt = false;
            }
        }
    }

    public bool IsSttOwnService
    {
        get => UseApiStt;
        set
        {
            if (value)
            {
                UseApiStt = true;
            }
        }
    }

    public bool ShowSttServiceSettings => IsDeveloperMode && UseApiStt;

    /// <summary>The list of local models: whenever speech is recognised on this computer.</summary>
    public bool ShowSpeechModels => CanUseLocalStt && !EffectiveUseApiStt;

    /// <summary>«Приватный»: whether the administrator has set the company's service up, in one line under the title.</summary>
    public string CompanyServiceText => L[CompanyService?.Configured == true ? "CompanyServiceText" : "CompanyServiceNotReady"];

    /// <summary>«Приватный», section «Речь»: on these computers (download a model below) or on the company's server.</summary>
    public string CompanySpeechText => L[CompanyService?.SpeechOnService == true ? "CompanySpeechService" : "CompanySpeechLocal"];


    /// <summary>Local recognition without a downloaded model: no transcript would appear.</summary>
    public bool IsSpeechModelMissing => ShowSpeechModels && SpeechModelCatalog.ResolveActiveModelPath(_settings, _projectRoot) == null;

    // ---------- AI models by plan ----------

    /// <summary>Бесплатный and Старт: the prepared models, by name only.</summary>
    public bool ShowReplioModelNames => IsManagedAi && !ShowReplioModelChoice;

    public string ReplioFastModelName => SelectedReplioFastModel?.Title ?? L["AiModelFastName"];

    public string ReplioAnalysisModelName => SelectedReplioAnalysisModel?.Title ?? L["AiModelSmartName"];

    public string AiModelsHint => IsSignedIn ? L["AiModelsUpgradeHint"] : L["AiModelsSignInHint"];

    // ---------- windows ----------

    /// <summary>«Скрывать Replio при демонстрации экрана»: the same setting as ShowInScreenShare, said positively.</summary>
    public bool HideInScreenShare
    {
        get => !ShowInScreenShare;
        set => ShowInScreenShare = !value;
    }

    /// <summary>The badge in the header says what is true now: hidden from the share or seen by the participants.</summary>
    public string ScreenShareBadgeText => ShowInScreenShare ? L["ShownInShareBadge"] : L["HiddenFromShareBadge"];

    /// <summary>A click on the badge switches «Скрывать Replio при демонстрации экрана» (saved like the checkbox).</summary>
    public RelayCommand ToggleScreenShareCommand { get; private set; } = null!;

    private void OnSettingsLayoutPlanChanged()
    {
        foreach (string name in new[]
        {
            nameof(IsLocalSpeechPlan), nameof(ShowSttQualityChoice), nameof(ShowSttCloudInfo), nameof(SttCloudInfoTitle),
            nameof(SttCloudInfoHint), nameof(ShowSttWhereChoice), nameof(IsSttOnComputer), nameof(IsSttOwnService),
            nameof(ShowSttServiceSettings), nameof(ShowSpeechModels), nameof(CompanyServiceText), nameof(CompanySpeechText), nameof(IsSpeechModelMissing), nameof(ShowReplioModelNames),
            nameof(ReplioFastModelName), nameof(ReplioAnalysisModelName), nameof(AiModelsHint), nameof(SpeechModelStatusText)
        })
        {
            OnPropertyChanged(name);
        }
    }

    private void OnSettingsLayoutLocalized()
    {
        foreach (string name in new[]
        {
            nameof(SettingsSectionTitle), nameof(SettingsSectionSubtitle), nameof(SettingsSaveStatus)
        })
        {
            OnPropertyChanged(name);
        }

        OnSettingsLayoutPlanChanged();
    }

    // ---------- saving ----------

    private void SaveSettings() => SaveSettingsCore(manual: true);

    /// <summary>
    /// Writes Settings. A hotkey that cannot be read keeps its previous value (and the error shows under the
    /// hotkeys); everything else is saved anyway, so one typo never loses other changes.
    /// </summary>
    private void SaveSettingsCore(bool manual)
    {
        _settingsAutoSaveTimer?.Stop();
        _settingsDirty = false;
        bool hotkeysValid = ValidateHotkeys(out List<string> errors);
        HotkeyErrorText = hotkeysValid ? string.Empty : string.Join(Environment.NewLine, errors);
        if (!hotkeysValid && manual)
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], HotkeyErrorText, true);
        }

        bool hotkeysChanged = false;
        if (hotkeysValid)
        {
            string before = HotkeySignature();
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
            hotkeysChanged = manual || before != HotkeySignature();
        }

        _settings.UiLanguage = SelectedUiLanguage;
        _settings.ThemeMode = SelectedThemeMode;
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
        _settings.VideoContextEnabled = VideoContextEnabled;
        _settings.VideoContextIntervalMs = VideoContextIntervalMs;
        _settings.VisualContextMaxFrames = VisualContextMaxFrames;
        _settings.VisualContextMaxImagesPerRequest = VisualContextMaxImagesPerRequest;

        _settingsService.Save(_settings);
        if (manual)
        {
            ApplyTheme();
        }

        AppendLog(manual ? "[settings] Saved." : "[settings] Saved automatically.");
        SettingsSaveStatus = SettingsSavedText;
        if (hotkeysChanged)
        {
            HotkeysChanged?.Invoke();
        }
    }

    private string HotkeySignature() => string.Join("|",
        _settings.ToggleOverlayHotkey, _settings.ToggleCaptureHotkey, _settings.AskHotkey, _settings.ClearChatHotkey,
        _settings.MoveLeftHotkey, _settings.MoveRightHotkey, _settings.MoveUpHotkey, _settings.MoveDownHotkey,
        _settings.ScrollUpHotkey, _settings.ScrollDownHotkey, _settings.SnapshotHotkey);

    /// <summary>«Вернуть стандартные»: the hotkeys a fresh install has.</summary>
    private void ResetHotkeys()
    {
        var defaults = new AppSettings();
        ToggleOverlayHotkeyText = defaults.ToggleOverlayHotkey;
        ToggleCaptureHotkeyText = defaults.ToggleCaptureHotkey;
        AskHotkeyText = defaults.AskHotkey;
        ClearChatHotkeyText = defaults.ClearChatHotkey;
        MoveLeftHotkeyText = defaults.MoveLeftHotkey;
        MoveRightHotkeyText = defaults.MoveRightHotkey;
        MoveUpHotkeyText = defaults.MoveUpHotkey;
        MoveDownHotkeyText = defaults.MoveDownHotkey;
        ScrollUpHotkeyText = defaults.ScrollUpHotkey;
        ScrollDownHotkeyText = defaults.ScrollDownHotkey;
        SnapshotHotkeyText = defaults.SnapshotHotkey;
        SaveSettingsCore(manual: false);
    }
}
