using System.Globalization;
using System.IO;

namespace CuePilotDesktop.Services;

/// <summary>A recognized piece of a media file, with its place in the file.</summary>
public sealed record MediaSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>How far the transcription is; <see cref="Total"/> is zero while the length of the file is unknown.</summary>
public sealed record MediaTranscriptionProgress(TimeSpan Done, TimeSpan Total);

public sealed class MediaTranscriptionRequest
{
    public string MediaPath { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string WhisperBin { get; init; } = string.Empty;
    public string WhisperModel { get; init; } = string.Empty;
    public string SttDir { get; init; } = string.Empty;
    public string Language { get; init; } = "ru";
    public double SilenceThreshold { get; init; } = 0.004;
    public string SttGlossary { get; init; } = string.Empty;
    public bool UseApiStt { get; init; }
    public string SttApiUrl { get; init; } = string.Empty;
    public string SttApiModel { get; init; } = string.Empty;
    public string SttApiKey { get; init; } = string.Empty;
}

public sealed class MediaTranscriptionResult
{
    public List<MediaSegment> Segments { get; } = [];
    public TimeSpan Duration { get; set; }
    public int FailedChunks { get; set; }
    public string LastError { get; set; } = string.Empty;
    public double CostRub { get; set; }
}

/// <summary>Sound of a media file as 16 kHz mono 16-bit samples.</summary>
public interface IMediaPcmReader : IDisposable
{
    /// <summary>Length of the file, zero when unknown.</summary>
    TimeSpan Duration { get; }

    /// <summary>Fills the buffer; 0 at the end of the file.</summary>
    int Read(short[] buffer);
}

/// <summary>
/// Phone version of the shared media transcription (same API, so «+ Медиа» in «База знаний» works unchanged).
/// The desktop runs DesktopCapture --transcribe-file; here the phone decodes the sound itself (Android MediaCodec,
/// set as <see cref="DecoderFactory"/>), cuts it into 15–28 s pieces at pauses like DesktopCapture and sends each
/// piece to the speech API.
/// </summary>
public static class MediaTranscriptionService
{
    private const int SampleRate = SpeechApiClient.SampleRate;
    private const int FrameSamples = SampleRate / 10;
    private const int MinPieceSeconds = 15;
    private const int MaxPieceSeconds = 28;

    public static readonly string[] Extensions =
    [
        ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wma", ".amr", ".aiff", ".aif", ".caf",
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".3gp", ".mpg", ".mpeg", ".mts", ".m2ts"
    ];

    /// <summary>Opens a media file for decoding (Android MediaCodec). Without it only WAV files are read.</summary>
    public static Func<string, IMediaPcmReader>? DecoderFactory { get; set; }

    public static string FileDialogPatterns => string.Join(";", Extensions.Select(e => "*" + e));

    public static bool IsMedia(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the transcription to the end. Cancelling stops after the current piece and throws
    /// <see cref="OperationCanceledException"/>; a file without readable sound throws with the reason.
    /// </summary>
    public static Task<MediaTranscriptionResult> TranscribeAsync(
        MediaTranscriptionRequest request,
        IProgress<MediaTranscriptionProgress>? progress,
        Action<string>? log,
        CancellationToken token) => Task.Run(() => Transcribe(request, progress, log, token), CancellationToken.None);

    private static MediaTranscriptionResult Transcribe(
        MediaTranscriptionRequest request,
        IProgress<MediaTranscriptionProgress>? progress,
        Action<string>? log,
        CancellationToken token)
    {
        // The view model has already chosen: the API when it is on and has a key, otherwise local whisper.cpp.
        using ISpeechRecognizer speech = request.UseApiStt
            ? new SpeechApiClient(request.SttApiUrl, request.SttApiKey, request.SttApiModel, request.Language, request.SttGlossary)
            : !string.IsNullOrWhiteSpace(request.WhisperModel) && LocalWhisper.IsAvailable
                ? new LocalWhisperRecognizer(request.WhisperModel, request.Language, request.SttGlossary)
                : throw new InvalidOperationException("No speech recognition: turn on the API or download a speech model in Settings.");
        var result = new MediaTranscriptionResult();
        log?.Invoke($"[media] Transcribing {Path.GetFileName(request.MediaPath)} ({speech.Name}).");
        using IMediaPcmReader reader = OpenReader(request.MediaPath);
        result.Duration = reader.Duration;
        progress?.Report(new MediaTranscriptionProgress(TimeSpan.Zero, result.Duration));

        double threshold = request.SilenceThreshold > 0 ? request.SilenceThreshold : 0.004;
        var frame = new short[FrameSamples];
        var piece = new List<short>(SampleRate * MaxPieceSeconds);
        long position = 0;
        long pieceStart = 0;
        int quietFrames = 0;
        int voicedFrames = 0;
        bool anySound = false;
        string previousText = string.Empty;

        void SendPiece()
        {
            if (piece.Count >= SampleRate / 2 && voicedFrames >= 3)
            {
                token.ThrowIfCancellationRequested();
                TimeSpan start = TimeSpan.FromSeconds(pieceStart / (double)SampleRate);
                TimeSpan end = TimeSpan.FromSeconds((pieceStart + piece.Count) / (double)SampleRate);
                try
                {
                    string text = speech.Transcribe(piece.ToArray(), previousText, token);
                    string cleaned = TranscriptTextPipeline.CleanRecognizedText(text);
                    if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Any(char.IsLetterOrDigit) && !speech.IsPromptEcho(cleaned))
                    {
                        previousText = text;
                        result.Segments.Add(new MediaSegment(start, end, cleaned));
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    result.FailedChunks++;
                    result.LastError = ex.Message;
                    log?.Invoke("[media] Piece failed: " + ex.Message);
                }

                TimeSpan done = result.Duration > TimeSpan.Zero && end > result.Duration ? result.Duration : end;
                progress?.Report(new MediaTranscriptionProgress(done, result.Duration));
            }

            pieceStart += piece.Count;
            piece.Clear();
            voicedFrames = 0;
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = reader.Read(frame);
            if (read <= 0)
            {
                break;
            }

            position += read;
            anySound = true;
            double sum = 0;
            for (int i = 0; i < read; i++)
            {
                double value = frame[i] / 32768d;
                sum += value * value;
                piece.Add(frame[i]);
            }

            bool quiet = Math.Sqrt(sum / read) < threshold;
            quietFrames = quiet ? quietFrames + 1 : 0;
            if (!quiet)
            {
                voicedFrames++;
            }

            if ((piece.Count >= SampleRate * MinPieceSeconds && quietFrames >= 3) || piece.Count >= SampleRate * MaxPieceSeconds)
            {
                SendPiece();
            }
        }

        SendPiece();
        if (!anySound)
        {
            throw new InvalidOperationException("The file has no sound track that can be read.");
        }

        TimeSpan length = TimeSpan.FromSeconds(position / (double)SampleRate);
        if (result.Duration == TimeSpan.Zero || length > result.Duration)
        {
            result.Duration = length;
        }

        result.CostRub = speech.CostRub;
        progress?.Report(new MediaTranscriptionProgress(result.Duration, result.Duration));
        return result;
    }

    private static IMediaPcmReader OpenReader(string path)
    {
        if (DecoderFactory != null)
        {
            return DecoderFactory(path);
        }

        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            return new WavPcmReader(path);
        }

        throw new InvalidOperationException("This file type cannot be decoded here.");
    }

    /// <summary>00:42 or 1:02:05.</summary>
    public static string FormatTime(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>A file: link that a Markdown link can carry: spaces and brackets escaped.</summary>
    public static string FileLink(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri.Replace("(", "%28").Replace(")", "%29");
}

/// <summary>Turns decoded PCM of any rate and channel count into 16 kHz mono (channel average, linear interpolation).</summary>
public sealed class PcmResampler
{
    private readonly Queue<short> _output = new();
    private int _sourceRate = SpeechApiClient.SampleRate;
    private int _channels = 1;
    private double _position;
    private double _previous;
    private bool _hasPrevious;

    public void SetFormat(int sampleRate, int channels)
    {
        _sourceRate = sampleRate > 0 ? sampleRate : SpeechApiClient.SampleRate;
        _channels = Math.Max(channels, 1);
    }

    public int Available => _output.Count;

    /// <summary>Adds interleaved 16-bit samples.</summary>
    public void Add(ReadOnlySpan<short> interleaved)
    {
        double step = _sourceRate / (double)SpeechApiClient.SampleRate;
        for (int i = 0; i + _channels <= interleaved.Length; i += _channels)
        {
            double mono = 0;
            for (int c = 0; c < _channels; c++)
            {
                mono += interleaved[i + c];
            }

            mono /= _channels;
            if (!_hasPrevious)
            {
                _previous = mono;
                _hasPrevious = true;
                _position = 0;
                continue;
            }

            // Output samples that fall between the previous input sample and this one.
            while (_position < 1)
            {
                double value = _previous + (mono - _previous) * _position;
                _output.Enqueue((short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
                _position += step;
            }

            _position -= 1;
            _previous = mono;
        }
    }

    public int Take(short[] buffer)
    {
        int count = Math.Min(buffer.Length, _output.Count);
        for (int i = 0; i < count; i++)
        {
            buffer[i] = _output.Dequeue();
        }

        return count;
    }
}

/// <summary>PCM WAV files (16-bit, 32-bit float), without the platform decoder.</summary>
public sealed class WavPcmReader : IMediaPcmReader
{
    private readonly BinaryReader _reader;
    private readonly PcmResampler _resampler = new();
    private readonly int _bits;
    private readonly bool _float;
    private long _remaining;

    public WavPcmReader(string path)
    {
        _reader = new BinaryReader(File.OpenRead(path));
        if (new string(_reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidOperationException("Not a WAV file.");
        }

        _reader.ReadInt32();
        _reader.ReadChars(4);
        int rate = SpeechApiClient.SampleRate;
        int channels = 1;
        while (true)
        {
            string id = new(_reader.ReadChars(4));
            int size = _reader.ReadInt32();
            if (id == "fmt ")
            {
                short format = _reader.ReadInt16();
                channels = _reader.ReadInt16();
                rate = _reader.ReadInt32();
                _reader.ReadInt32();
                _reader.ReadInt16();
                _bits = _reader.ReadInt16();
                _float = format == 3;
                _reader.BaseStream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                _remaining = size;
                break;
            }
            else
            {
                _reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        if (!(_bits == 16 || (_float && _bits == 32)))
        {
            throw new InvalidOperationException("Only 16-bit or float WAV files can be read.");
        }

        _resampler.SetFormat(rate, channels);
        Duration = TimeSpan.FromSeconds(_remaining / (double)(rate * channels * (_bits / 8)));
    }

    public TimeSpan Duration { get; }

    public int Read(short[] buffer)
    {
        var block = new short[4096];
        while (_resampler.Available < buffer.Length && _remaining > 0)
        {
            int count = 0;
            while (count < block.Length && _remaining > 0)
            {
                if (_float)
                {
                    block[count++] = (short)Math.Clamp(_reader.ReadSingle() * 32767, short.MinValue, short.MaxValue);
                    _remaining -= 4;
                }
                else
                {
                    block[count++] = _reader.ReadInt16();
                    _remaining -= 2;
                }
            }

            _resampler.Add(block.AsSpan(0, count));
        }

        return _resampler.Take(buffer);
    }

    public void Dispose() => _reader.Dispose();
}
