using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CuePilotDesktop.Models;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.ViewModels;

/// <summary>What the 📷 button of the recording indicator shows: taking, saved (with its number) or not taken.</summary>
public enum SnapshotStage
{
    Busy,
    Saved,
    Failed
}

/// <summary>
/// «Снимок» keeps the screenshot with the session: it shows on the «Снимки» tab and in the transcript, the summary links
/// to it, and a click opens it in the default picture viewer.
/// </summary>
public partial class MainViewModel
{
    private bool _isSnapshotBusy;
    private bool _hasSelectedSessionScreenshots;

    public ObservableCollection<SessionScreenshot> SelectedSessionScreenshots { get; } = [];

    public bool HasSelectedSessionScreenshots
    {
        get => _hasSelectedSessionScreenshots;
        private set
        {
            if (_hasSelectedSessionScreenshots == value)
            {
                return;
            }

            _hasSelectedSessionScreenshots = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedSessionScreenshotsTitle));
        }
    }

    public string SelectedSessionScreenshotsTitle => string.Format(L["ScreenshotsCount"], SelectedSessionScreenshots.Count);

    /// <summary>
    /// Raised on the UI thread while a snapshot is taken: the indicator button shows it at once, since the overlay
    /// with its notice is usually hidden during a call. The number is the screenshot's number in the session (0 if none).
    /// </summary>
    public event Action<SnapshotStage, int>? SnapshotStageChanged;

    /// <summary>The 📷 button and the hotkey: always answers, also when screenshots are off in the settings.</summary>
    public void TakeSnapshot() => CaptureSnapshotNow();

    /// <summary>Parameter: a <see cref="SessionScreenshot"/>, a screenshot line of the transcript or a file path.</summary>
    public RelayCommand OpenScreenshotCommand { get; private set; } = null!;

    private void InitializeScreenshots()
    {
        OpenScreenshotCommand = new RelayCommand(OpenScreenshot);
    }

    private async void CaptureSnapshotNow()
    {
        if (!IsCapturing || string.IsNullOrWhiteSpace(_currentSessionId) || _isSnapshotBusy)
        {
            return;
        }

        if (!CaptureScreenContextEnabled)
        {
            // Used to do nothing at all, so the button looked broken.
            SnapshotStageChanged?.Invoke(SnapshotStage.Failed, 0);
            OverlayNoticeRequested?.Invoke(L["SnapshotDisabledNotice"]);
            return;
        }

        string sessionId = _currentSessionId;
        _isSnapshotBusy = true;
        SnapshotStageChanged?.Invoke(SnapshotStage.Busy, 0);
        try
        {
            // Screen capture, window text and the full-size JPEG take a moment: off the UI thread.
            (SessionVisualFrame? frame, string keptPath) = await Task.Run(() => CaptureAndKeepSnapshot(sessionId));
            if (frame == null)
            {
                SnapshotStageChanged?.Invoke(SnapshotStage.Failed, 0);
                OverlayNoticeRequested?.Invoke(L["SnapshotSkippedNotice"]);
                AppendLog("[visual] Manual snapshot skipped.");
                return;
            }

            OnVisualFrameCaptured(frame);
            int number = keptPath.Length > 0 && sessionId == _currentSessionId ? AddScreenshot(sessionId, frame, keptPath) : 0;
            SnapshotStageChanged?.Invoke(SnapshotStage.Saved, number);
            OverlayNoticeRequested?.Invoke(number > 0 ? string.Format(L["SnapshotSavedNotice"], number) : L["SnapshotTakenNotice"]);
            AppendLog(keptPath.Length > 0 ? $"[visual] Manual snapshot {number} captured and kept." : "[visual] Manual snapshot captured (not kept).");
        }
        catch (Exception ex)
        {
            SnapshotStageChanged?.Invoke(SnapshotStage.Failed, 0);
            OverlayNoticeRequested?.Invoke(L["SnapshotSkippedNotice"]);
            AppendLog("[visual] Manual snapshot failed: " + ex.Message);
        }
        finally
        {
            _isSnapshotBusy = false;
        }
    }

    /// <summary>Background part: the frame for the AI context plus the copy kept for the user (full size when the platform gives one).</summary>
    private (SessionVisualFrame? Frame, string KeptPath) CaptureAndKeepSnapshot(string sessionId)
    {
        if (!_visualContextCapture.CaptureSnapshot("manual", true, out SessionVisualFrame? frame) || frame == null)
        {
            return (null, string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(frame.FullImagePath) && File.Exists(frame.FullImagePath))
        {
            return (frame, frame.FullImagePath);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(frame.FilePath) && File.Exists(frame.FilePath))
            {
                string folder = SessionScreenshots.Folder(_visualContextCapture.StorageRoot, sessionId);
                Directory.CreateDirectory(folder);
                string kept = Path.Combine(folder, Path.GetFileName(frame.FilePath));
                File.Copy(frame.FilePath, kept, overwrite: true);
                return (frame, kept);
            }
        }
        catch (Exception)
        {
            // The frame still serves as AI context; only the kept copy is missing.
        }

        return (frame, string.Empty);
    }

    /// <summary>Keeps the screenshot with the session and returns its number (0 when the session is gone).</summary>
    private int AddScreenshot(string sessionId, SessionVisualFrame frame, string path)
    {
        string title = frame.WindowTitle?.Trim() ?? string.Empty;
        if (title.StartsWith("CuePilot", StringComparison.OrdinalIgnoreCase))
        {
            // Taken with the button of our own window: its title says nothing about the screen.
            title = string.Empty;
        }

        var shot = new SessionScreenshot { Timestamp = frame.Timestamp, FilePath = path, WindowTitle = title };
        bool english = IsEnglishInterface;
        string speaker = L["ScreenshotSpeaker"];
        SessionTranscriptItem? line = _sessionLifecycleService.AddScreenshot(
            sessionId, shot, kept => SessionScreenshots.TranscriptLine(kept, speaker, english));
        if (line == null)
        {
            return 0;
        }

        if (SelectedSession?.Id == sessionId)
        {
            SelectedSessionTranscript.Add(line);
            shot.Caption = SessionScreenshots.Label(shot, english, withTitle: false);
            SelectedSessionScreenshots.Add(shot);
            HasSelectedSessionScreenshots = true;
            OnPropertyChanged(nameof(SelectedSessionScreenshotsTitle));
        }

        return shot.Number;
    }

    private void RefreshSelectedSessionScreenshots(SessionSummary? session)
    {
        SelectedSessionScreenshots.Clear();
        bool english = IsEnglishInterface;
        foreach (SessionScreenshot shot in session?.Screenshots.OrderBy(s => s.Number) ?? Enumerable.Empty<SessionScreenshot>())
        {
            shot.Caption = SessionScreenshots.Label(shot, english, withTitle: false);
            SelectedSessionScreenshots.Add(shot);
        }

        HasSelectedSessionScreenshots = SelectedSessionScreenshots.Count > 0;
        OnPropertyChanged(nameof(SelectedSessionScreenshotsTitle));
        if (!HasSelectedSessionScreenshots && SelectedSessionTab == "Screenshots")
        {
            SelectedSessionTab = "Summary";
        }
    }

    private void OpenScreenshot(object? parameter)
    {
        string? path = parameter switch
        {
            SessionScreenshot shot => shot.FilePath,
            SessionTranscriptItem line => line.ScreenshotPath,
            string text => text,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], L["ScreenshotMissing"], false);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], ex.Message, true);
            AppendLog("[visual] Screenshot could not be opened: " + ex.Message);
        }
    }
}
