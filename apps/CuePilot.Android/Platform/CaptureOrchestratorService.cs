using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace CuePilotDesktop.Services;

/// <summary>
/// Phone version of the shared capture service (same class and events, so MainViewModel works unchanged).
/// The desktop one runs DesktopCapture; here the phone microphone is read in-process (16 kHz mono), cut into
/// 5–10 s chunks at pauses like DesktopCapture does, and each chunk goes to the speech API (OpenAI-compatible
/// /audio/transcriptions). Recognized lines come out as <see cref="TranscriptReceived"/> from the microphone.
/// </summary>
public class CaptureOrchestratorService
{
    private const int SampleRate = SpeechApiClient.SampleRate;
    private const int FrameSamples = SampleRate / 10;

    private readonly object _gate = new();
    private CaptureRun? _run;
    private bool _heldStop;

    /// <summary>Opens the microphone. Set by the Android activity; tests pass their own source.</summary>
    public static Func<IPcmSource>? PcmSourceFactory { get; set; }

    /// <summary>Called with true when a recording starts and false when it has fully stopped (the foreground service).</summary>
    public static Action<bool>? RecordingStateHook { get; set; }

    public event EventHandler<string>? LogLine;
    public event EventHandler<CaptureStats>? StatsUpdated;
    public event EventHandler<TranscriptLine>? TranscriptReceived;
    public event EventHandler<bool>? RunningStateChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _run != null;
            }
        }
    }

    public void Start(CaptureStartOptions options)
    {
        CaptureRun run;
        lock (_gate)
        {
            if (_run != null)
            {
                return;
            }

            _heldStop = false;
            IPcmSource source = PcmSourceFactory?.Invoke() ?? throw new InvalidOperationException("Microphone is not available.");
            run = _run = new CaptureRun(this, options, source);
        }

        RecordingStateHook?.Invoke(true);
        try
        {
            run.Start();
        }
        catch
        {
            Finish(run);
            throw;
        }

        RunningStateChanged?.Invoke(this, true);
        Log(options.UseApiStt
            ? "[service] Phone microphone capture started, speech recognition through " + options.SttApiModel + "."
            : "[service] Phone microphone capture started without speech recognition (no API key).");
    }

    /// <summary>No DesktopCapture on the phone: media files in the knowledge base cannot be transcribed by it.</summary>
    public static string? FindCaptureExecutable(string workingDirectory) => null;

    /// <summary>Stops the microphone; the last chunk and the ones still queued are recognized in the background.</summary>
    public void Stop()
    {
        bool held;
        lock (_gate)
        {
            held = _heldStop && _run == null;
            _heldStop = false;
        }

        if (held)
        {
            // Drained by StopAndDrainAsync: the view model learns now that the recording is over.
            RunningStateChanged?.Invoke(this, false);
            Log("[service] Capture stopped.");
            return;
        }

        _ = StopAndDrainAsync(TimeSpan.FromSeconds(20), holdRunningState: false);
    }

    /// <summary>
    /// Stops the microphone and waits until the last chunks are recognized (at most <paramref name="timeout"/>),
    /// so the end of the conversation reaches the session before it is closed. The view model still sees the
    /// recording running until <see cref="Stop"/> (its StopCapture) is called, so the session is closed after the lines.
    /// </summary>
    public Task StopAndDrainAsync(TimeSpan timeout, bool holdRunningState = true)
    {
        CaptureRun? run;
        lock (_gate)
        {
            run = _run;
            if (run != null)
            {
                run.HoldRunningState = holdRunningState;
            }
        }

        return run == null ? Task.CompletedTask : run.StopAsync(timeout);
    }

    public void Kill()
    {
        CaptureRun? run;
        lock (_gate)
        {
            run = _run;
        }

        run?.Abort();
    }

    private void Finish(CaptureRun run)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_run, run))
            {
                return;
            }

            _run = null;
            _heldStop = run.HoldRunningState;
        }

        RecordingStateHook?.Invoke(false);
        if (run.HoldRunningState)
        {
            Log("[service] Microphone stopped, the last chunks are recognized.");
            return;
        }

        RunningStateChanged?.Invoke(this, false);
        Log("[service] Capture stopped.");
    }

    private void Log(string line) => LogLine?.Invoke(this, line);

    private void EmitTranscript(string text, DateTime timestamp)
    {
        string cleanedText = TranscriptTextPipeline.CleanRecognizedText(text);
        if (string.IsNullOrWhiteSpace(cleanedText))
        {
            return;
        }

        TranscriptReceived?.Invoke(this, new TranscriptLine
        {
            Source = TranscriptSource.Mic,
            RawText = text,
            SpeakerName = "MIC",
            Text = cleanedText,
            FormattedText = TranscriptTextPipeline.BuildFormattedLine(TranscriptSource.Mic, cleanedText),
            SummaryReadyText = TranscriptTextPipeline.BuildSummaryReadyText("MIC", cleanedText),
            Timestamp = timestamp
        });
    }

    /// <summary>One recording: the microphone thread cuts chunks, the recognizer task sends them in order.</summary>
    private sealed class CaptureRun
    {
        private readonly CaptureOrchestratorService _owner;
        private readonly CaptureStartOptions _options;
        private readonly IPcmSource _source;
        private readonly BlockingCollection<Chunk> _queue = new();
        private readonly CancellationTokenSource _abort = new();
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SpeechApiClient _speech;
        private Thread? _readerThread;
        private Task? _recognizer;
        private volatile bool _stopRequested;
        private int _dropped;
        private string _previousText = string.Empty;
        private DateTime _oldestPendingAt = DateTime.MinValue;

        public bool HoldRunningState { get; set; }

        public CaptureRun(CaptureOrchestratorService owner, CaptureStartOptions options, IPcmSource source)
        {
            _owner = owner;
            _options = options;
            _source = source;
            _speech = new SpeechApiClient(options.SttApiUrl, options.SttApiKey, options.SttApiModel, options.Language, options.SttGlossary);
        }

        public void Start()
        {
            _source.Start();
            _recognizer = Task.Run(RecognizeLoop);
            _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "CuePilot microphone" };
            _readerThread.Start();
        }

        public async Task StopAsync(TimeSpan timeout)
        {
            _stopRequested = true;
            Task finished = _finished.Task;
            if (await Task.WhenAny(finished, Task.Delay(timeout)) != finished)
            {
                _owner.Log("[stt] Recognition of the last chunks took too long: they are skipped.");
                Abort();
            }
        }

        public void Abort()
        {
            _stopRequested = true;
            _abort.Cancel();
            _queue.CompleteAdding();
        }

        private void ReadLoop()
        {
            var frame = new short[FrameSamples];
            var chunk = new List<short>(SampleRate * Math.Max(_options.MaxChunkSeconds, 1));
            int minSamples = SampleRate * Math.Max(_options.ChunkSeconds, 1);
            int maxSamples = SampleRate * Math.Max(_options.MaxChunkSeconds, _options.ChunkSeconds + 1);
            double threshold = _options.SilenceThreshold > 0 ? _options.SilenceThreshold : 0.004;
            int quietFrames = 0;
            int voicedFrames = 0;
            DateTime chunkStartedAt = DateTime.Now;
            DateTime statsAt = DateTime.UtcNow;
            double statsSum = 0;
            int statsFrames = 0;
            double statsPeak = 0;

            try
            {
                while (!_stopRequested && !_abort.IsCancellationRequested)
                {
                    int read = _source.Read(frame);
                    if (read <= 0)
                    {
                        if (read < 0)
                        {
                            _owner.Log("[service] Microphone read failed: " + read);
                            Thread.Sleep(50);
                        }

                        continue;
                    }

                    (double rms, double peak) = Measure(frame, read);
                    if (chunk.Count == 0)
                    {
                        chunkStartedAt = DateTime.Now;
                    }

                    for (int i = 0; i < read; i++)
                    {
                        chunk.Add(frame[i]);
                    }

                    bool quiet = rms < threshold;
                    quietFrames = quiet ? quietFrames + 1 : 0;
                    if (!quiet)
                    {
                        voicedFrames++;
                    }

                    // Cut at a pause (0.3 s of quiet) once the chunk is long enough, or at the maximum length.
                    if ((chunk.Count >= minSamples && quietFrames >= 3) || chunk.Count >= maxSamples)
                    {
                        Enqueue(chunk, voicedFrames, chunkStartedAt);
                        chunk.Clear();
                        voicedFrames = 0;
                    }

                    statsSum += rms;
                    statsFrames++;
                    statsPeak = Math.Max(statsPeak, peak);
                    if (DateTime.UtcNow - statsAt >= TimeSpan.FromSeconds(1))
                    {
                        PublishStats(statsFrames == 0 ? 0 : statsSum / statsFrames, statsPeak);
                        statsAt = DateTime.UtcNow;
                        statsSum = 0;
                        statsFrames = 0;
                        statsPeak = 0;
                    }
                }

                if (!_abort.IsCancellationRequested)
                {
                    Enqueue(chunk, voicedFrames, chunkStartedAt);
                }
            }
            catch (Exception ex)
            {
                _owner.Log("[service] Microphone stopped with an error: " + ex.Message);
            }
            finally
            {
                try
                {
                    _source.Stop();
                    _source.Dispose();
                }
                catch (Exception ex)
                {
                    _owner.Log("[service] Microphone release failed: " + ex.Message);
                }

                if (!_queue.IsAddingCompleted)
                {
                    _queue.CompleteAdding();
                }
            }
        }

        private void Enqueue(List<short> samples, int voicedFrames, DateTime startedAt)
        {
            // Under 0.3 s of sound in the whole chunk: silence, not worth an API call.
            if (samples.Count < SampleRate / 2 || voicedFrames < 3 || _queue.IsAddingCompleted)
            {
                return;
            }

            lock (_queue)
            {
                if (_queue.Count == 0)
                {
                    _oldestPendingAt = DateTime.Now;
                }
            }

            try
            {
                _queue.Add(new Chunk(samples.ToArray(), startedAt, DateTime.Now));
            }
            catch (InvalidOperationException)
            {
                // Aborted meanwhile: the chunk is not needed.
            }
        }

        private void RecognizeLoop()
        {
            try
            {
                foreach (Chunk chunk in _queue.GetConsumingEnumerable())
                {
                    if (_abort.IsCancellationRequested)
                    {
                        break;
                    }

                    // Far behind the conversation: older chunks are dropped so hints stay about the present.
                    if (_options.MaxSttLagSeconds > 0 && (DateTime.Now - chunk.EndedAt).TotalSeconds > _options.MaxSttLagSeconds && _queue.Count > 0)
                    {
                        _dropped++;
                        _owner.Log("[stt] Chunk dropped: recognition lags behind the conversation.");
                        continue;
                    }

                    byte[] wav = SpeechApiClient.BuildWav(chunk.Samples);
                    if (_options.KeepChunks && !string.IsNullOrWhiteSpace(_options.SttDir))
                    {
                        TrySaveChunk(wav, chunk.StartedAt);
                    }

                    if (!_options.UseApiStt)
                    {
                        continue;
                    }

                    try
                    {
                        string text = _speech.Transcribe(wav, _previousText, _abort.Token);
                        if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetterOrDigit) || _speech.IsPromptEcho(text))
                        {
                            continue;
                        }

                        _previousText = text;
                        _owner.EmitTranscript(text, chunk.StartedAt);
                    }
                    catch (Exception ex) when (!_abort.IsCancellationRequested)
                    {
                        _owner.Log("[stt] Recognition failed: " + ex.Message);
                    }
                }
            }
            finally
            {
                _owner.Finish(this);
                _finished.TrySetResult();
            }
        }

        private void PublishStats(double rms, double peak)
        {
            double lag = 0;
            int queued = _queue.Count;
            if (queued > 0)
            {
                lock (_queue)
                {
                    lag = (DateTime.Now - _oldestPendingAt).TotalSeconds;
                }
            }

            _owner.StatsUpdated?.Invoke(_owner, new CaptureStats
            {
                // The shared screen shows «KB/s» levels; for the phone microphone it is a 0–30 loudness scale.
                MicAverageKb = Math.Min(30, rms * 600),
                MicPeak = peak,
                SttQueueLength = queued,
                SttLagSeconds = lag,
                SttCostRub = _speech.CostRub
            });
        }

        private void TrySaveChunk(byte[] wav, DateTime startedAt)
        {
            try
            {
                Directory.CreateDirectory(_options.SttDir);
                File.WriteAllBytes(Path.Combine(_options.SttDir, "mic-" + startedAt.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".wav"), wav);
            }
            catch (Exception ex)
            {
                _owner.Log("[stt] Chunk was not saved: " + ex.Message);
            }
        }

        private static (double Rms, double Peak) Measure(short[] frame, int count)
        {
            double sum = 0;
            double peak = 0;
            for (int i = 0; i < count; i++)
            {
                double value = frame[i] / 32768d;
                sum += value * value;
                peak = Math.Max(peak, Math.Abs(value));
            }

            return (Math.Sqrt(sum / Math.Max(count, 1)), peak);
        }

        private sealed record Chunk(short[] Samples, DateTime StartedAt, DateTime EndedAt);
    }
}

/// <summary>16 kHz mono 16-bit microphone samples. <see cref="Read"/> blocks until a frame is ready.</summary>
public interface IPcmSource : IDisposable
{
    void Start();

    /// <summary>Fills the buffer, returns the number of samples read (negative on error).</summary>
    int Read(short[] buffer);

    void Stop();
}

public class CaptureStartOptions
{
    public string WorkingDirectory { get; set; } = string.Empty;
    public string WhisperBin { get; set; } = string.Empty;
    public string WhisperModel { get; set; } = string.Empty;
    public string SttDir { get; set; } = string.Empty;
    public int ChunkSeconds { get; set; } = 5;
    public int MaxChunkSeconds { get; set; } = 10;
    public string Language { get; set; } = "ru";
    public double SilenceThreshold { get; set; } = 0.004;
    public int MaxSttLagSeconds { get; set; } = 60;
    public bool KeepChunks { get; set; }
    public bool UseApiStt { get; set; }
    public string SttApiUrl { get; set; } = string.Empty;
    public string SttApiModel { get; set; } = string.Empty;
    public string SttApiKey { get; set; } = string.Empty;
    public string SttGlossary { get; set; } = string.Empty;
}

public class CaptureStats
{
    public double SystemAverageKb { get; set; }
    public double SystemPeak { get; set; }
    public double MicAverageKb { get; set; }
    public double MicPeak { get; set; }
    public int SttQueueLength { get; set; }
    public double SttLagSeconds { get; set; }

    /// <summary>Cumulative API transcription cost of the current recording, RUB.</summary>
    public double SttCostRub { get; set; }
}

public class TranscriptLine
{
    public TranscriptSource Source { get; set; }
    public string SpeakerName { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string FormattedText { get; set; } = string.Empty;
    public string SummaryReadyText { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

public enum TranscriptSource
{
    System,
    Mic
}
