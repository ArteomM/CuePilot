using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CuePilotDesktop.Models;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.ViewModels;

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

    /// <summary>Parameter: a <see cref="SessionScreenshot"/>, a screenshot line of the transcript or a file path.</summary>
    public RelayCommand OpenScreenshotCommand { get; private set; } = null!;

    private void InitializeScreenshots()
    {
        OpenScreenshotCommand = new RelayCommand(OpenScreenshot);
    }

    private async void CaptureSnapshotNow()
    {
        if (!IsCapturing || !CaptureScreenContextEnabled || string.IsNullOrWhiteSpace(_currentSessionId) || _isSnapshotBusy)
        {
            return;
        }

        string sessionId = _currentSessionId;
        _isSnapshotBusy = true;
        try
        {
            // Screen capture, window text and the full-size JPEG take a moment: off the UI thread.
            (SessionVisualFrame? frame, string keptPath) = await Task.Run(() => CaptureAndKeepSnapshot(sessionId));
            if (frame == null)
            {
                OverlayNoticeRequested?.Invoke(L["SnapshotSkippedNotice"]);
                AppendLog("[visual] Manual snapshot skipped.");
                return;
            }

            OnVisualFrameCaptured(frame);
            if (keptPath.Length > 0 && sessionId == _currentSessionId)
            {
                AddScreenshot(sessionId, frame, keptPath);
            }

            OverlayNoticeRequested?.Invoke(L["SnapshotTakenNotice"]);
            AppendLog(keptPath.Length > 0 ? "[visual] Manual snapshot captured and kept." : "[visual] Manual snapshot captured (not kept).");
        }
        catch (Exception ex)
        {
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

    private void AddScreenshot(string sessionId, SessionVisualFrame frame, string path)
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
            return;
        }

        if (SelectedSession?.Id == sessionId)
        {
            SelectedSessionTranscript.Add(line);
            shot.Caption = SessionScreenshots.Label(shot, english, withTitle: false);
            SelectedSessionScreenshots.Add(shot);
            HasSelectedSessionScreenshots = true;
            OnPropertyChanged(nameof(SelectedSessionScreenshotsTitle));
        }
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
