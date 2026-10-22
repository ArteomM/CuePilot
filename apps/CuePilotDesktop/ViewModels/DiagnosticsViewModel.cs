using CuePilotDesktop.Services;

namespace CuePilotDesktop.ViewModels;

public class DiagnosticsViewModel : ObservableObject
{
    private readonly EnvironmentDiagnosticsService _diagnosticsService;

    public DiagnosticsViewModel(EnvironmentDiagnosticsService diagnosticsService)
    {
        _diagnosticsService = diagnosticsService;
        Refresh();
    }

    private string _dotnetMessage = "Unknown";
    private string _dotnetBrush = DefaultWarn;
    private string _captureBackendMessage = "Unknown";
    private string _captureBackendBrush = DefaultWarn;
    private string _whisperBinMessage = "Unknown";
    private string _whisperBinBrush = DefaultWarn;
    private string _whisperModelMessage = "Unknown";
    private string _whisperModelBrush = DefaultWarn;
    private string _sttDirMessage = "Unknown";
    private string _sttDirBrush = DefaultWarn;

    private const string DefaultOk = "#065F46";
    private const string DefaultWarn = "#92400E";
    private const string DefaultError = "#991B1B";

    public string DotnetMessage { get => _dotnetMessage; private set { _dotnetMessage = value; OnPropertyChanged(); } }
    public string DotnetBrush { get => _dotnetBrush; private set { _dotnetBrush = value; OnPropertyChanged(); } }

    public string CaptureBackendMessage { get => _captureBackendMessage; private set { _captureBackendMessage = value; OnPropertyChanged(); } }
    public string CaptureBackendBrush { get => _captureBackendBrush; private set { _captureBackendBrush = value; OnPropertyChanged(); } }

    public string WhisperBinMessage { get => _whisperBinMessage; private set { _whisperBinMessage = value; OnPropertyChanged(); } }
    public string WhisperBinBrush { get => _whisperBinBrush; private set { _whisperBinBrush = value; OnPropertyChanged(); } }

    public string WhisperModelMessage { get => _whisperModelMessage; private set { _whisperModelMessage = value; OnPropertyChanged(); } }
    public string WhisperModelBrush { get => _whisperModelBrush; private set { _whisperModelBrush = value; OnPropertyChanged(); } }

    public string SttDirMessage { get => _sttDirMessage; private set { _sttDirMessage = value; OnPropertyChanged(); } }
    public string SttDirBrush { get => _sttDirBrush; private set { _sttDirBrush = value; OnPropertyChanged(); } }

    public bool IsReadyForCapture =>
        !HasCriticalError(_captureBackendBrush) &&
        !HasCriticalError(_sttDirBrush);

    public void Refresh()
    {
        var snapshot = _diagnosticsService.GetSnapshot();
        ApplyState(snapshot.Dotnet, v => DotnetMessage = v, b => DotnetBrush = b);
        ApplyState(snapshot.CaptureBackend, v => CaptureBackendMessage = v, b => CaptureBackendBrush = b);
        ApplyState(snapshot.WhisperBin, v => WhisperBinMessage = v, b => WhisperBinBrush = b);
        ApplyState(snapshot.WhisperModel, v => WhisperModelMessage = v, b => WhisperModelBrush = b);
        ApplyState(snapshot.SttDir, v => SttDirMessage = v, b => SttDirBrush = b);
        OnPropertyChanged(nameof(IsReadyForCapture));
    }

    private static void ApplyState(DiagnosticState state, Action<string> setMessage, Action<string> setBrush)
    {
        setMessage(state.Message);
        setBrush(state.Severity switch
        {
            DiagnosticSeverity.Ok => DefaultOk,
            DiagnosticSeverity.Error => DefaultError,
            _ => DefaultWarn
        });
    }

    private static bool HasCriticalError(string brush) => string.Equals(brush, DefaultError, StringComparison.Ordinal);
}
