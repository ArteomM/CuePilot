using System.ComponentModel;
using Avalonia.Threading;
using ReplioDesktop.Services;

namespace Replio.Android.Platform;

/// <summary>
/// What the recording screen says while Replio records, in a few plain words: «Слушаю…», «Слышу и записываю»,
/// «Микрофон не слышит звук». A failed piece of recognition is not news (the next one usually works): only two
/// failures in a row show a calm line, and never the error text itself, which goes to the log.
/// </summary>
public sealed class RecordingMonitor : INotifyPropertyChanged
{
    private const double SoundPeak = 0.004;
    private static readonly TimeSpan SilenceWarningAfter = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan ErrorShownFor = TimeSpan.FromSeconds(30);
    private const int ErrorsBeforeWarning = 2;

    private readonly CaptureOrchestratorService _capture;
    private DateTime _startedAt;
    private DateTime? _lastSoundAt;
    private int _recognized;
    private int _empty;
    private FriendlyError.Kind _lastError;
    private int _errorsInRow;
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
        _lastError = FriendlyError.Kind.None;
        _errorsInRow = 0;
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
        if (piece.Error != null)
        {
            PhonePlatform.Log("[stt] Piece failed: " + piece.Error);
            _lastError = FriendlyError.Classify(piece.Error);
            _errorsInRow++;
            _lastErrorAt = DateTime.Now;
        }
        else if (piece.Text.Length > 0)
        {
            _recognized++;
            _errorsInRow = 0;
            _lastSoundAt = DateTime.Now;
        }
        else
        {
            _empty++;
            _errorsInRow = 0;
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
        else if (_errorsInRow >= ErrorsBeforeWarning && now - _lastErrorAt < ErrorShownFor)
        {
            Show(MobileText.Get(_lastError switch
            {
                FriendlyError.Kind.Network => "MonitorNetwork",
                FriendlyError.Kind.SignIn => "MonitorSignIn",
                FriendlyError.Kind.Limit => "MonitorLimit",
                _ => "MonitorTrouble"
            }), problem: true);
        }
        else if ((_lastSoundAt == null && now - _startedAt > SilenceWarningAfter) ||
                 (_lastSoundAt != null && now - _lastSoundAt.Value > TimeSpan.FromSeconds(40)))
        {
            Show(MobileText.Get("MonitorSilence"), problem: true);
        }
        else if (_queue > 0 && _lag >= 30)
        {
            Show(MobileText.Get("MonitorLaggingBad"), problem: true);
        }
        else if (_queue > 0 && _lag >= 10)
        {
            Show(MobileText.Get("MonitorLagging"), problem: false);
        }
        else if (_recognized > 0)
        {
            Show(MobileText.Get("MonitorRecognized"), problem: false);
        }
        else if (_empty > 1)
        {
            Show(MobileText.Get("MonitorNoSpeech"), problem: false);
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
