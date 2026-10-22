namespace CuePilotDesktop.Services;

public class AppSettings
{
    public const string GeneralModeName = "General";
    public const string DefaultModeName = "Default";
    public const int CurrentSettingsVersion = 2;

    public const string PolzaPresetName = "Polza.ai";
    public const string PolzaBaseUrl = "https://polza.ai/api/v1";
    public const string PolzaFastModel = "google/gemini-3.8-flash";
    public const string PolzaAnalysisModel = "anthropic/claude-sonnet-5.5";
    public const string PolzaSttModel = "openai/gpt-4o-mini-transcribe";

    public const string SttProviderLocal = "local";
    public const string SttProviderApi = "api";

    public int SettingsVersion { get; set; }

    public string UiLanguage { get; set; } = "ru";
    public string ThemeMode { get; set; } = "dark";
    public string CaptureLanguage { get; set; } = "ru";
    public string OutputLanguage { get; set; } = "ru";
    public string MicParticipantName { get; set; } = "Я";
    public string SystemParticipantName { get; set; } = "Собеседник";
    public int ChunkSeconds { get; set; } = 5;
    public int MaxChunkSeconds { get; set; } = 10;
    public int TranscriptBufferLimit { get; set; } = 300;
    public string ToggleOverlayHotkey { get; set; } = "Ctrl+/";
    public string ToggleCaptureHotkey { get; set; } = "Ctrl+Shift+/";
    public string AskHotkey { get; set; } = "Ctrl+Enter";
    public string ClearChatHotkey { get; set; } = "Ctrl+R";
    public string MoveLeftHotkey { get; set; } = "Alt+Left";
    public string MoveRightHotkey { get; set; } = "Alt+Right";
    public string MoveUpHotkey { get; set; } = "Alt+Up";
    public string MoveDownHotkey { get; set; } = "Alt+Down";
    public string ScrollUpHotkey { get; set; } = "Ctrl+Shift+Up";
    public string ScrollDownHotkey { get; set; } = "Ctrl+Shift+Down";
    public string SnapshotHotkey { get; set; } = "Ctrl+Shift+S";
    public bool SmartModeEnabled { get; set; }
    public string ApiPresetName { get; set; } = PolzaPresetName;
    public string ApiProvider { get; set; } = PolzaPresetName;
    public string ApiKeyHeaderName { get; set; } = "Authorization";
    public string ApiKeyPrefix { get; set; } = "Bearer";
    public string ChatCompletionsPath { get; set; } = "/chat/completions";
    // Property names are kept for compatibility with existing settings.json files.
    public string OpenAiModel { get; set; } = PolzaFastModel;
    public string AnalysisModel { get; set; } = PolzaAnalysisModel;
    public string OpenAiBaseUrl { get; set; } = PolzaBaseUrl;
    public string OpenAiApiKey { get; set; } = string.Empty;
    public bool ShowInScreenShare { get; set; }
    public bool ShowInTaskbar { get; set; } = true;
    public bool InvisibilityModeEnabled { get; set; } = true;
    public bool AutoLaunchEnabled { get; set; }
    public bool PermissionsConfirmed { get; set; }
    public bool CaptureScreenContextEnabled { get; set; } = true;
    public string SelectedMonitor { get; set; } = "Primary";
    public string GeneralRealtimePrompt { get; set; } = string.Empty;
    public string GeneralNotesTemplate { get; set; } = string.Empty;
    public string GeneralReferenceFile { get; set; } = string.Empty;
    public string DefaultRealtimePrompt { get; set; } = string.Empty;
    public string DefaultNotesTemplate { get; set; } = string.Empty;
    public string DefaultReferenceFile { get; set; } = string.Empty;
    public string SelectedModeName { get; set; } = GeneralModeName;
    public bool VideoContextEnabled { get; set; } = true;
    public int VideoContextIntervalMs { get; set; } = 2000;
    public int VisualContextMaxFrames { get; set; } = 24;
    public int VisualContextMaxImagesPerRequest { get; set; } = 3;
    public string? WhisperBinPath { get; set; }
    public string? WhisperModelPath { get; set; }
    /// <summary>Speech model chosen in Settings (file name from SpeechModelCatalog).</summary>
    public string SpeechModelFileName { get; set; } = SpeechModelCatalog.DefaultFileName;
    public string? SttDirectoryPath { get; set; }
    public string SttProvider { get; set; } = SttProviderLocal;
    public string SttApiPresetName { get; set; } = string.Empty;
    public string SttApiBaseUrl { get; set; } = PolzaBaseUrl;
    public string SttApiModel { get; set; } = PolzaSttModel;
    public string SttApiKey { get; set; } = string.Empty;
    public string SttGlossary { get; set; } = string.Empty;
    public double SttSilenceThreshold { get; set; } = 0.004;
    public int SttMaxLagSeconds { get; set; } = 60;
    public bool KeepAudioChunks { get; set; }
}
