using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Replio.Android.Platform;
using Replio.Android.Platform.Calls;
using ReplioDesktop.Services;

namespace Replio.Android;

/// <summary>
/// The process: Avalonia and the app (its view model) start here, not with the screen, so the background service and
/// the button over a call keep working after Android closes the app screen. Platform hooks that need no screen are
/// set here; the ones that need it go through <see cref="MainActivity.Current"/>.
/// </summary>
[Application]
public class ReplioApplication : AvaloniaAndroidApplication<App>
{
    private static bool _hiddenFromCapture;

    protected ReplioApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    /// <summary>The last «hide from screen recordings» choice, applied to every new app screen.</summary>
    public static bool HiddenFromCapture => _hiddenFromCapture;

    public override void OnCreate()
    {
        CaptureOrchestratorService.PcmSourceFactory = () => new PhoneAudioSource();
        MediaTranscriptionService.DecoderFactory = path => new AndroidMediaDecoder(path);
        LocalWhisper.JavaLoader = name =>
        {
            try
            {
                Java.Lang.JavaSystem.LoadLibrary(name);
                return true;
            }
            catch (Java.Lang.Throwable)
            {
                return false;
            }
        };
        CaptureOrchestratorService.RecordingStateHook = ReplioService.SetMicrophone;
        PhonePlatform.HasMicrophonePermission = () =>
            CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) == global::Android.Content.PM.Permission.Granted;
        PhonePlatform.RequestMicrophonePermissionAsync = () =>
            MainActivity.Current?.RequestMicrophonePermissionAsync() ?? Task.FromResult(PhonePlatform.HasMicrophonePermission());
        PhonePlatform.ShareText = (title, text) => MainActivity.Current?.ShareText(title, text);
        PhonePlatform.SetHiddenFromScreenCapture = hidden =>
        {
            _hiddenFromCapture = hidden;
            MainActivity.Current?.SetHiddenFromScreenCapture(hidden);
        };
        PhonePlatform.OpenFile = path => MainActivity.Current?.OpenFile(path) ?? false;
        PhonePlatform.Log = line => global::Android.Util.Log.Info("Replio", line);
        ReplioDesktop.ViewModels.MainViewModel.ExternalUrlOpener = url =>
        {
            MainActivity.OpenUri(this, url);
            return true;
        };

        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder);
}
