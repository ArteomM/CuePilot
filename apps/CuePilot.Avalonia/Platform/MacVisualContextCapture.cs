using CuePilotDesktop.Models;
using CuePilotDesktop.Services;

namespace CuePilot.Avalonia.Platform;

/// <summary>
/// Placeholder until stage 4 (ScreenCaptureKit snapshots): screen context is reported as unavailable
/// and nothing is stored.
/// </summary>
public sealed class MacVisualContextCapture : IVisualContextCapture
{
    public event EventHandler<SessionVisualFrame>? KeyFrameCaptured { add { } remove { } }

    public event EventHandler<string>? LogLine;

    public string StorageRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CuePilot", "visual-context");

    public void Start(string sessionId, int intervalMs) =>
        LogLine?.Invoke(this, "[visual] Screen context is not available on this platform yet.");

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
