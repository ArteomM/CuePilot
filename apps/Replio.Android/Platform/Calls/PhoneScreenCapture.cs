using ReplioDesktop.Models;
using ReplioDesktop.Services;

namespace Replio.Android.Platform.Calls;

/// <summary>
/// «Снимок экрана» on the phone: the screen under the Replio menu, taken by the «Слушать звонок» accessibility
/// service (Android 11+). Only on request, never in the background on its own.
/// </summary>
internal sealed class PhoneScreenCapture : IVisualContextCapture
{
    public event EventHandler<SessionVisualFrame>? KeyFrameCaptured { add { } remove { } }

    public event EventHandler<string>? LogLine;

    public static bool IsAvailable =>
        global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.R &&
        ReplioAccessibilityService.Instance != null;

    public string StorageRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Replio", "captures");

    public void Start(string sessionId, int intervalMs)
    {
    }

    public void Stop()
    {
    }

    public bool CaptureSnapshot(string source, bool force, out SessionVisualFrame? frame)
    {
        frame = null;
        if (ReplioAccessibilityService.Instance is not { } service)
        {
            LogLine?.Invoke(this, "[visual] Screenshot needs «Слушать звонок» (Android 11+).");
            return false;
        }

        DateTime now = DateTime.Now;
        string path = Path.Combine(StorageRoot, "phone", $"snap-{now:yyyyMMdd-HHmmss-fff}.jpg");
        string? saved = service.TakeScreenshotTo(path, TimeSpan.FromSeconds(5));
        if (saved == null)
        {
            return false;
        }

        frame = new SessionVisualFrame
        {
            Timestamp = now,
            FilePath = saved,
            Source = source,
            ChangeReason = "manual",
            WindowTitle = ReplioAccessibilityService.ForegroundAppName
        };
        return true;
    }

    public void DeleteFrameFiles(IEnumerable<SessionVisualFrame> frames)
    {
        foreach (SessionVisualFrame frame in frames)
        {
            TryDelete(frame.FilePath);
        }
    }

    public void DeleteSessionFiles(string sessionId)
    {
    }

    public void CleanupOrphans(IReadOnlyCollection<SessionVisualFrame> referencedFrames, IReadOnlyCollection<string> knownSessionIds)
    {
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Still in use: left for later.
        }
    }
}
