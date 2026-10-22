using System.IO;

namespace CuePilotDesktop.Services;

public class EnvironmentDiagnosticsService
{
    private readonly string _projectRoot;

    public EnvironmentDiagnosticsService(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

    /// <summary>Temporary audio chunks: deleted after transcription, so they live outside the install/repo folder.</summary>
    public static string DefaultSttChunksDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CuePilot",
        "stt-chunks");

    public DiagnosticsSnapshot GetSnapshot()
    {
        string desktopCaptureProject = Path.Combine(_projectRoot, "apps", "DesktopCapture", "DesktopCapture.csproj");
        string installedCaptureExe = Path.Combine(_projectRoot, PlatformPaths.CaptureExecutableName);
        string captureBackend = File.Exists(installedCaptureExe) ? installedCaptureExe : desktopCaptureProject;
        string whisperBin = PlatformPaths.DefaultWhisperBin(_projectRoot);
        string? whisperModel = SpeechModelCatalog.ResolveActiveModelPath(new AppSettings(), _projectRoot);
        string sttDir = DefaultSttChunksDirectory;

        bool sttDirReady = EnsureDirectory(sttDir, out string sttDirError);

        return new DiagnosticsSnapshot
        {
            Dotnet = new DiagnosticState("OK: .NET host is available for app startup", DiagnosticSeverity.Ok),
            CaptureBackend = File.Exists(captureBackend)
                ? new DiagnosticState("OK: " + captureBackend, DiagnosticSeverity.Ok)
                : new DiagnosticState("ERROR: missing " + captureBackend, DiagnosticSeverity.Error),
            WhisperBin = File.Exists(whisperBin)
                ? new DiagnosticState("OK: " + whisperBin, DiagnosticSeverity.Ok)
                : new DiagnosticState("WARN: missing " + whisperBin + " (capture will work without STT)", DiagnosticSeverity.Warn),
            WhisperModel = whisperModel != null
                ? new DiagnosticState("OK: " + whisperModel, DiagnosticSeverity.Ok)
                : new DiagnosticState("WARN: speech model is not downloaded (Settings, speech recognition): " + SpeechModelCatalog.UserModelsDirectory, DiagnosticSeverity.Warn),
            SttDir = sttDirReady
                ? new DiagnosticState("OK: " + sttDir, DiagnosticSeverity.Ok)
                : new DiagnosticState("ERROR: " + sttDirError, DiagnosticSeverity.Error)
        };
    }

    private static bool EnsureDirectory(string path, out string error)
    {
        try
        {
            Directory.CreateDirectory(path);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = "cannot create " + path + " (" + ex.Message + ")";
            return false;
        }
    }
}

public class DiagnosticsSnapshot
{
    public DiagnosticState Dotnet { get; set; } = new(string.Empty, DiagnosticSeverity.Warn);
    public DiagnosticState CaptureBackend { get; set; } = new(string.Empty, DiagnosticSeverity.Warn);
    public DiagnosticState WhisperBin { get; set; } = new(string.Empty, DiagnosticSeverity.Warn);
    public DiagnosticState WhisperModel { get; set; } = new(string.Empty, DiagnosticSeverity.Warn);
    public DiagnosticState SttDir { get; set; } = new(string.Empty, DiagnosticSeverity.Warn);
}

public class DiagnosticState
{
    public DiagnosticState(string message, DiagnosticSeverity severity)
    {
        Message = message;
        Severity = severity;
    }

    public string Message { get; }
    public DiagnosticSeverity Severity { get; }
}

public enum DiagnosticSeverity
{
    Ok,
    Warn,
    Error
}
