using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using CuePilot.Android.Platform;
using CuePilotDesktop.Services;

namespace CuePilot.Android;

[Activity(
    Label = "CuePilot",
    Theme = "@style/CuePilotTheme",
    Icon = "@mipmap/icon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public class MainActivity : AvaloniaMainActivity<App>
{
    private const int PermissionRequestCode = 4101;
    private TaskCompletionSource<bool>? _permissionRequest;

    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        CaptureOrchestratorService.PcmSourceFactory = () => new AndroidMicrophone();
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
        CaptureOrchestratorService.RecordingStateHook = recording =>
        {
            if (recording)
            {
                MicrophoneService.Start(this);
            }
            else
            {
                MicrophoneService.Stop(this);
            }
        };
        PhonePlatform.HasMicrophonePermission = () => HasMicrophonePermission;
        PhonePlatform.RequestMicrophonePermissionAsync = RequestMicrophonePermissionAsync;
        PhonePlatform.ShareText = ShareText;
        base.OnCreate(savedInstanceState);
    }

    public bool HasMicrophonePermission =>
        CheckSelfPermission(Manifest.Permission.RecordAudio) == Permission.Granted;

    /// <summary>Asks for the microphone (and, on Android 13+, notifications for the recording indicator).</summary>
    public Task<bool> RequestMicrophonePermissionAsync()
    {
        if (HasMicrophonePermission)
        {
            return Task.FromResult(true);
        }

        _permissionRequest?.TrySetResult(false);
        _permissionRequest = new TaskCompletionSource<bool>();
        var permissions = new List<string> { Manifest.Permission.RecordAudio };
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            permissions.Add(Manifest.Permission.PostNotifications);
        }

        RequestPermissions(permissions.ToArray(), PermissionRequestCode);
        return _permissionRequest.Task;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == PermissionRequestCode)
        {
            _permissionRequest?.TrySetResult(HasMicrophonePermission);
            _permissionRequest = null;
        }
    }

    /// <summary>Opens a link or a file outside the app (a browser, a player).</summary>
    public void OpenUri(string uri)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(uri));
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
        }
        catch (Exception)
        {
            // No app for this link.
        }
    }

    /// <summary>Shares text through the Android share sheet (mail, messengers).</summary>
    public void ShareText(string title, string text)
    {
        var intent = new Intent(Intent.ActionSend);
        intent.SetType("text/plain");
        intent.PutExtra(Intent.ExtraSubject, title);
        intent.PutExtra(Intent.ExtraText, text);
        StartActivity(Intent.CreateChooser(intent, title));
    }
}
