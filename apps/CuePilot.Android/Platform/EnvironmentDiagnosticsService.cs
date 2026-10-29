using System.IO;

namespace CuePilotDesktop.Services;

/// <summary>
/// Phone version of the shared diagnostics: there is no DesktopCapture and no local whisper, the phone records
/// with its own microphone (Platform/CaptureOrchestratorService.cs) and recognizes speech through the API.
/// </summary>
public class EnvironmentDiagnosticsService
{
    public EnvironmentDiagnosticsService(string projectRoot)
    {
    }

    /// <summary>Temporary audio chunks of a recording (only kept when «Keep chunks» is on).</summary>
    public static string DefaultSttChunksDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CuePilot",
        "stt-chunks");

    public DiagnosticsSnapshot GetSnapshot()
    {
        string sttDir = DefaultSttChunksDirectory;
        bool sttDirReady = EnsureDirectory(sttDir, out string sttDirError);
        return new DiagnosticsSnapshot
        {
            Dotnet = new DiagnosticState("OK: .NET for Android", DiagnosticSeverity.Ok),
            CaptureBackend = new DiagnosticState("OK: phone microphone", DiagnosticSeverity.Ok),
            WhisperBin = new DiagnosticState("OK: speech recognition through the API", DiagnosticSeverity.Ok),
            WhisperModel = new DiagnosticState("OK: speech recognition through the API", DiagnosticSeverity.Ok),
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
