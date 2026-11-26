using Android.Content;
using Android.Media;
using Android.OS;

namespace Replio.Android.Platform.Calls;

/// <summary>
/// Notices calls by the audio mode: a phone call sets IN_CALL, Zoom, Телемост, Teams, WhatsApp, Telegram and other
/// call apps set IN_COMMUNICATION. Needs no permission (no access to the call log or the phone state) and sees every
/// call app the same way. Polled once a second on the main thread: a cheap read of one system value.
/// </summary>
public sealed class CallWatcher : IDisposable
{
    private const int PollMs = 1000;
    // The mode flickers when a call app switches devices: the call counts as over after 3 quiet seconds.
    private const int EndAfterQuietPolls = 3;

    private readonly AudioManager? _audio;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly Action _poll;
    private int _quietPolls;
    private bool _running;

    public CallWatcher(Context context)
    {
        _audio = context.GetSystemService(Context.AudioService) as AudioManager;
        _poll = Poll;
    }

    public bool InCall { get; private set; }

    /// <summary>True during a phone (cellular) call, false for a call in an app.</summary>
    public bool IsPhoneCall { get; private set; }

    public event Action<bool>? CallStateChanged;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _handler.Post(_poll);
    }

    public void Stop()
    {
        _running = false;
        _handler.RemoveCallbacks(_poll);
    }

    /// <summary>The speakerphone is on: the microphone then hears the other side of the call too.</summary>
    public bool IsSpeakerOn()
    {
        if (_audio == null)
        {
            return false;
        }

        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
            {
                return _audio.CommunicationDevice?.Type == AudioDeviceType.BuiltinSpeaker;
            }

#pragma warning disable CA1422 // SpeakerphoneOn is the only way before Android 12.
            return _audio.SpeakerphoneOn;
#pragma warning restore CA1422
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Poll()
    {
        if (!_running)
        {
            return;
        }

        try
        {
            Mode mode = _audio?.Mode ?? Mode.Normal;
            bool active = mode == Mode.InCall || mode == Mode.InCommunication;
            if (active)
            {
                _quietPolls = 0;
                IsPhoneCall = mode == Mode.InCall;
                if (!InCall)
                {
                    InCall = true;
                    CallStateChanged?.Invoke(true);
                }
            }
            else if (InCall && ++_quietPolls >= EndAfterQuietPolls)
            {
                InCall = false;
                IsPhoneCall = false;
                CallStateChanged?.Invoke(false);
            }
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[calls] " + ex.Message);
        }

        _handler.PostDelayed(_poll, PollMs);
    }

    public void Dispose()
    {
        Stop();
        _handler.Dispose();
    }
}
