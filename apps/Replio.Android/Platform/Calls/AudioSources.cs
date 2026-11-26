using Android.Media;
using Android.Media.Projection;
using Android.OS;
using ReplioDesktop.Services;

namespace Replio.Android.Platform.Calls;

/// <summary>What Replio records from right now, in plain words for the button menu, the notification and Home.</summary>
public static class AudioSourceStatus
{
    private static string _microphone = string.Empty;
    private static bool _playback;
    private static bool _silenced;

    /// <summary>«Микрофон», «Микрофон (распознавание)»…; empty when not recording.</summary>
    public static string Microphone => _microphone;

    /// <summary>The sound of other apps (video, broadcasts) is mixed in.</summary>
    public static bool Playback => _playback;

    /// <summary>Every microphone mode gave pure digital silence: Android blocks it (a call without «Слушать звонок»).</summary>
    public static bool Silenced => _silenced;

    public static event Action? Changed;

    public static string Describe()
    {
        if (_microphone.Length == 0 && !_playback)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (_microphone.Length > 0)
        {
            parts.Add(_silenced ? CallText.Get("SourceMicBlocked") : _microphone);
        }

        if (_playback)
        {
            parts.Add(CallText.Get("SourcePlayback"));
        }

        return CallText.Get("SourcePrefix") + string.Join(" + ", parts);
    }

    internal static void Set(string? microphone = null, bool? playback = null, bool? silenced = null)
    {
        _microphone = microphone ?? _microphone;
        _playback = playback ?? _playback;
        _silenced = silenced ?? _silenced;
        Changed?.Invoke();
    }

    internal static void Reset()
    {
        _microphone = string.Empty;
        _playback = false;
        _silenced = false;
        Changed?.Invoke();
    }
}

/// <summary>
/// The phone's sound for a recording: the microphone, switched to another microphone mode by itself when Android
/// hands it pure silence (that is how the system blocks the microphone during someone else's call), plus, when the
/// user allowed it, the sound other apps play (video, broadcasts, a meeting in a browser) through
/// AudioPlaybackCapture. Both are mixed into one 16 kHz mono stream.
/// </summary>
public sealed class PhoneAudioSource : IPcmSource
{
    private const int SampleRate = 16000;
    // About 3 s of exact zeros (100 ms reads) before the next microphone mode is tried.
    private const int SilentReadsBeforeSwitch = 30;

    private static readonly (AudioSource Source, string Key)[] Modes =
    [
        (AudioSource.Mic, "SourceMic"),
        (AudioSource.VoiceRecognition, "SourceMicRecognition"),
        (AudioSource.VoiceCommunication, "SourceMicCall"),
        (AudioSource.Camcorder, "SourceMicCamera"),
        (AudioSource.Unprocessed, "SourceMicRaw")
    ];

    private static PhoneAudioSource? _current;
    private static MediaProjection? _projection;

    private readonly object _gate = new();
    private AudioRecord? _mic;
    private AudioRecord? _playback;
    private short[] _playbackBuffer = [];
    private int _mode;
    private int _silentReads;
    private bool _started;

    public PhoneAudioSource()
    {
        _mic = Open(_mode);
        if (_mic == null)
        {
            throw new InvalidOperationException("The microphone is busy or not allowed.");
        }

        _current = this;
        AudioSourceStatus.Set(microphone: CallText.Get(Modes[_mode].Key), silenced: false);
        if (_projection != null)
        {
            AttachPlayback(_projection);
        }
    }

    /// <summary>True when the sound of other apps can be captured at all (Android 10+).</summary>
    public static bool PlaybackSupported => Build.VERSION.SdkInt >= BuildVersionCodes.Q;

    /// <summary>The user allowed capturing the screen sound: mixed into the running recording and the next ones.</summary>
    public static void SetProjection(MediaProjection? projection)
    {
        _projection = projection;
        PhoneAudioSource? current = _current;
        if (current == null)
        {
            return;
        }

        if (projection != null)
        {
            current.AttachPlayback(projection);
        }
        else
        {
            current.DetachPlayback();
        }
    }

    public static bool HasProjection => _projection != null;

    public void Start()
    {
        lock (_gate)
        {
            _started = true;
            _mic?.StartRecording();
            _playback?.StartRecording();
        }
    }

    public int Read(short[] buffer)
    {
        AudioRecord? mic;
        AudioRecord? playback;
        lock (_gate)
        {
            mic = _mic;
            playback = _playback;
        }

        int read = mic?.Read(buffer, 0, buffer.Length) ?? 0;
        if (read <= 0)
        {
            return read;
        }

        CheckSilence(buffer, read);
        if (playback != null)
        {
            if (_playbackBuffer.Length < read)
            {
                _playbackBuffer = new short[read];
            }

            int got = playback.Read(_playbackBuffer, 0, read);
            for (int i = 0; i < got && i < read; i++)
            {
                buffer[i] = (short)Math.Clamp(buffer[i] + _playbackBuffer[i], short.MinValue, short.MaxValue);
            }
        }

        return read;
    }

    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
            StopRecord(_mic);
            StopRecord(_playback);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _mic?.Release();
            _mic = null;
            _playback?.Release();
            _playback = null;
        }

        if (ReferenceEquals(_current, this))
        {
            _current = null;
            AudioSourceStatus.Reset();
        }
    }

    /// <summary>Pure zeros mean Android gives this app no microphone now: the next microphone mode is tried.</summary>
    private void CheckSilence(short[] buffer, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (buffer[i] != 0)
            {
                if (_silentReads >= SilentReadsBeforeSwitch || AudioSourceStatus.Silenced)
                {
                    AudioSourceStatus.Set(silenced: false);
                }

                _silentReads = 0;
                return;
            }
        }

        if (++_silentReads < SilentReadsBeforeSwitch)
        {
            return;
        }

        _silentReads = 0;
        for (int next = _mode + 1; next < Modes.Length; next++)
        {
            AudioRecord? record = Open(next);
            if (record == null)
            {
                continue;
            }

            lock (_gate)
            {
                StopRecord(_mic);
                _mic?.Release();
                _mic = record;
                _mode = next;
                if (_started)
                {
                    record.StartRecording();
                }
            }

            PhonePlatform.Log("[audio] Silence from the microphone, switched to " + Modes[next].Source + ".");
            AudioSourceStatus.Set(microphone: CallText.Get(Modes[next].Key));
            return;
        }

        // Every mode is silenced: back to the first one on the next round, and the user is told why.
        AudioSourceStatus.Set(silenced: true);
    }

    private static AudioRecord? Open(int mode)
    {
        try
        {
            int minBuffer = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
            var record = new AudioRecord(Modes[mode].Source, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(minBuffer, SampleRate * 2));
            if (record.State == State.Initialized)
            {
                return record;
            }

            record.Release();
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[audio] " + Modes[mode].Source + " is not available: " + ex.Message);
        }

        return null;
    }

    private void AttachPlayback(MediaProjection projection)
    {
        if (!PlaybackSupported)
        {
            return;
        }

        try
        {
#pragma warning disable CA1416 // Checked above: Android 10+.
            var config = new AudioPlaybackCaptureConfiguration.Builder(projection)
                .AddMatchingUsage(AudioUsageKind.Media)
                .AddMatchingUsage(AudioUsageKind.Game)
                .AddMatchingUsage(AudioUsageKind.Unknown)
                .Build();
            var format = new AudioFormat.Builder()
                .SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(SampleRate)!
                .SetChannelMask((ChannelOut)(int)ChannelIn.Mono)!
                .Build();
            AudioRecord record = new AudioRecord.Builder()
                .SetAudioFormat(format)!
                .SetBufferSizeInBytes(SampleRate * 4)!
                .SetAudioPlaybackCaptureConfig(config)!
                .Build()!;
#pragma warning restore CA1416
            lock (_gate)
            {
                StopRecord(_playback);
                _playback?.Release();
                _playback = record;
                if (_started)
                {
                    record.StartRecording();
                }
            }

            AudioSourceStatus.Set(playback: true);
            PhonePlatform.Log("[audio] Sound of other apps is mixed in.");
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[audio] Sound of other apps cannot be captured: " + ex.Message);
            AudioSourceStatus.Set(playback: false);
        }
    }

    private void DetachPlayback()
    {
        lock (_gate)
        {
            StopRecord(_playback);
            _playback?.Release();
            _playback = null;
        }

        AudioSourceStatus.Set(playback: false);
    }

    private static void StopRecord(AudioRecord? record)
    {
        try
        {
            if (record?.RecordingState == RecordState.Recording)
            {
                record.Stop();
            }
        }
        catch (Exception)
        {
            // Already released.
        }
    }
}
