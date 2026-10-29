using Android.Media;
using CuePilotDesktop.Services;

namespace CuePilot.Android.Platform;

/// <summary>The phone microphone: 16 kHz mono 16-bit, with the system gain control and noise suppression.</summary>
public sealed class AndroidMicrophone : IPcmSource
{
    private const int SampleRate = 16000;
    private readonly AudioRecord _record;

    public AndroidMicrophone()
    {
        int minBuffer = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        _record = new AudioRecord(AudioSource.Mic, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(minBuffer, SampleRate * 2));
        if (_record.State != State.Initialized)
        {
            _record.Release();
            throw new InvalidOperationException("The microphone is busy or not allowed.");
        }
    }

    public void Start() => _record.StartRecording();

    public int Read(short[] buffer) => _record.Read(buffer, 0, buffer.Length);

    public void Stop()
    {
        if (_record.RecordingState == RecordState.Recording)
        {
            _record.Stop();
        }
    }

    public void Dispose() => _record.Release();
}
