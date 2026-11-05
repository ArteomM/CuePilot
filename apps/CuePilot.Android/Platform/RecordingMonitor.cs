using System.ComponentModel;
using Avalonia.Threading;
using CuePilotDesktop.Services;

namespace CuePilot.Android.Platform;

/// <summary>
/// What the recording bar shows while CuePilot records: the microphone level and, in words, what happens to the
/// speech (listening, recognizing, how far behind, the last error, silence from a microphone taken by a call).
/// The phone has no log on screen, so a recording that gives no text says why here.
/// </summary>
public sealed class RecordingMonitor : INotifyPropertyChanged
{
    private const double SoundPeak = 0.004;
    private static readonly TimeSpan SilenceWarningAfter = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan ErrorShownFor = TimeSpan.FromSeconds(30);

    private readonly CaptureOrchestratorService _capture;
    private DateTime _startedAt;
    private DateTime? _lastSoundAt;
    private int _recognized;
    private int _empty;
    private SpeechPieceResult? _lastPiece;
    private string? _lastError;
    private DateTime _lastErrorAt;
    private int _queue;
    private double _lag;
    private double _level;
    private string _engine = string.Empty;
    private string _status = string.Empty;
    private bool _isProblem;

    public RecordingMonitor(CaptureOrchestratorService capture)
    {
        _capture = capture;
        capture.StatsUpdated += (_, stats) => Dispatcher.UIThread.Post(() => OnStats(stats));
        capture.PieceRecognized += (_, piece) => Dispatcher.UIThread.Post(() => OnPiece(piece));
        capture.RunningStateChanged += (_, running) => Dispatcher.UIThread.Post(() =>
        {
            if (running)
            {
                Reset();
            }
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Microphone level 0…1 (square root of the peak, so speech across the table still moves the bar).</summary>
    public double Level
    {
        get => _level;
        private set => Set(ref _level, value, nameof(Level));
    }

    /// <summary>«Распознаёт: whisper.cpp Small».</summary>
    public string EngineText
    {
        get => _engine;
        private set => Set(ref _engine, value, nameof(EngineText));
    }

    public string StatusText
    {
        get => _status;
        private set => Set(ref _status, value, nameof(StatusText));
    }

    /// <summary>The recording gives no text and the user has to do something (the status is shown in red).</summary>
    public bool IsProblem
    {
        get => _isProblem;
        private set => Set(ref _isProblem, value, nameof(IsProblem));
    }

    private void Reset()
    {
        _startedAt = DateTime.Now;
        _lastSoundAt = null;
        _recognized = 0;
        _empty = 0;
        _lastPiece = null;
        _lastError = null;
        _queue = 0;
        _lag = 0;
        Level = 0;
        string name = _capture.ActiveSpeechName;
        EngineText = name.Length == 0 ? string.Empty : string.Format(MobileText.Get("MonitorEngine"), name);
        Update();
    }

    private void OnStats(CaptureStats stats)
    {
        if (!_capture.IsRunning)
        {
            return;
        }

        Level = Math.Clamp(Math.Sqrt(stats.MicPeak) * 1.4, 0, 1);
        if (stats.MicPeak >= SoundPeak)
        {
            _lastSoundAt = DateTime.Now;
        }

        _queue = stats.SttQueueLength;
        _lag = stats.SttLagSeconds;
        Update();
    }

    private void OnPiece(SpeechPieceResult piece)
    {
        _lastPiece = piece;
        if (piece.Error != null)
        {
            _lastError = piece.Error;
            _lastErrorAt = DateTime.Now;
        }
        else if (piece.Text.Length > 0)
        {
            _recognized++;
            _lastError = null;
            _lastSoundAt = DateTime.Now;
        }
        else
        {
            _empty++;
        }

        Update();
    }

    private void Update()
    {
        DateTime now = DateTime.Now;
        if (_capture.ActiveSpeechName.Length == 0)
        {
            Show(MobileText.Get("MonitorNoEngine"), problem: true);
        }
        else if (_lastError != null && now - _lastErrorAt < ErrorShownFor)
        {
            Show(string.Format(MobileText.Get("MonitorError"), _lastError), problem: true);
        }
        else if ((_lastSoundAt == null && now - _startedAt > SilenceWarningAfter) ||
                 (_lastSoundAt != null && now - _lastSoundAt.Value > TimeSpan.FromSeconds(40)))
        {
            Show(MobileText.Get("MonitorSilence"), problem: true);
        }
        else if (_queue > 0 && _lag >= 8)
        {
            Show(string.Format(MobileText.Get("MonitorLagging"), _lag), problem: _lag >= 30);
        }
        else if (_recognized > 0 && _lastPiece != null)
        {
            Show(string.Format(MobileText.Get("MonitorRecognized"), _recognized, _lastPiece.AudioSeconds, _lastPiece.RecognitionSeconds), problem: false);
        }
        else if (_empty > 0)
        {
            Show(string.Format(MobileText.Get("MonitorNoSpeech"), _empty), problem: false);
        }
        else if (_queue > 0 || _lastSoundAt != null)
        {
            Show(MobileText.Get("MonitorHearing"), problem: false);
        }
        else
        {
            Show(MobileText.Get("MonitorListening"), problem: false);
        }
    }

    private void Show(string text, bool problem)
    {
        StatusText = text;
        IsProblem = problem;
    }

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
