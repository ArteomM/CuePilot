using CuePilotDesktop.Models;

namespace CuePilotDesktop.Services;

/// <summary>
/// Screen snapshots for AI context. Windows: VisualContextCaptureService (GDI + UI Automation);
/// other platforms supply their own implementation.
/// </summary>
public interface IVisualContextCapture
{
    event EventHandler<SessionVisualFrame>? KeyFrameCaptured;
    event EventHandler<string>? LogLine;

    string StorageRoot { get; }

    void Start(string sessionId, int intervalMs);
    void Stop();
    bool CaptureSnapshot(string source, bool force, out SessionVisualFrame? frame);
    void DeleteFrameFiles(IEnumerable<SessionVisualFrame> frames);
    void DeleteSessionFiles(string sessionId);
    void CleanupOrphans(IReadOnlyCollection<SessionVisualFrame> referencedFrames, IReadOnlyCollection<string> knownSessionIds);
}
