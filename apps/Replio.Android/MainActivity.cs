using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using Replio.Android.Platform;
using Replio.Android.Platform.Calls;

namespace Replio.Android;

[Activity(
    Label = "Replio",
    Theme = "@style/ReplioTheme",
    Icon = "@mipmap/icon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    Exported = true,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
// Audio and video shared from another app (a call recording, a voice message) go into the topic and are transcribed.
[IntentFilter(
    new[] { Intent.ActionSend, Intent.ActionSendMultiple },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeTypes = new[] { "audio/*", "video/*" })]
public class MainActivity : AvaloniaMainActivity<App>
{
    /// <summary>Started by a tap on the call after a reboot: the screen comes up only to create Replio, then steps back.</summary>
    public const string BackgroundExtra = "replio_background";

    /// <summary>A meeting to open (the «Итоги встречи готовы» notification).</summary>
    public const string OpenSessionExtra = "replio_open_session";

    private const int PermissionRequestCode = 4101;
    private TaskCompletionSource<bool>? _permissionRequest;
    private bool _returnToCall;
    private bool _keepScreenOn;

    public static MainActivity? Current { get; private set; }

    /// <summary>Replio's screen is what the user sees now (no notification for what they already see).</summary>
    public static bool IsInForeground { get; private set; }

    /// <summary>
    /// Creates the app screen out of sight: after a reboot the button on a call works before Replio was opened. The
    /// screen steps back to the call as soon as it is ready. False when Android refused to start it.
    /// </summary>
    public static bool WakeInBackground()
    {
        Context context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(MainActivity));
        intent.PutExtra(BackgroundExtra, true);
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.NoAnimation);
        try
        {
            // The accessibility service may start a screen from the background; so may an app with the overlay permission.
            (ReplioAccessibilityService.Instance as Context ?? context).StartActivity(intent);
            return true;
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[calls] The app screen could not be started: " + ex.Message);
            return false;
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        string? selfTestUrl = Intent?.GetStringExtra(SelfTest.SttExtra);
        if (!string.IsNullOrEmpty(selfTestUrl))
        {
            SelfTest.RunSpeechApi(selfTestUrl);
        }

#if REPLIO_DEMO
        if (Intent?.GetBooleanExtra("replio_demo_seed", false) == true)
        {
            DemoData.Seed();
        }
#endif

        base.OnCreate(savedInstanceState);
        SetHiddenFromScreenCapture(false);
        // Replio stays in the background from the first start on and waits for calls.
        ReplioService.Start(this);
        HandleShared(Intent);
        HandleLaunchExtras(Intent);
        ShowDemoPage(Intent);
    }

    public override bool DispatchTouchEvent(global::Android.Views.MotionEvent? ev)
    {
        TouchTracker.Track(ev, this);
        return base.DispatchTouchEvent(ev);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleShared(intent);
        HandleLaunchExtras(intent);
        ShowDemoPage(intent);
    }

    private void HandleLaunchExtras(Intent? intent)
    {
        if (intent == null)
        {
            return;
        }

        if (intent.GetBooleanExtra(BackgroundExtra, false))
        {
            _returnToCall = true;
            intent.RemoveExtra(BackgroundExtra);
        }

        string? session = intent.GetStringExtra(OpenSessionExtra);
        if (!string.IsNullOrEmpty(session))
        {
            intent.RemoveExtra(OpenSessionExtra);
            Views.MainView.OpenSessionFromOutside(session);
        }
    }

    /// <summary>The live meeting screen keeps the phone awake: the timer and hints stay readable on a long call.</summary>
    public void SetKeepScreenOn(bool on)
    {
        if (on == _keepScreenOn)
        {
            return;
        }

        _keepScreenOn = on;
        RunOnUiThread(() =>
        {
            if (on)
            {
                Window?.AddFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
            }
            else
            {
                Window?.ClearFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
            }
        });
    }

    /// <summary>Emulator build of CI only: «--es replio_demo live» opens that page for a screenshot.</summary>
    private static void ShowDemoPage(Intent? intent)
    {
#if REPLIO_DEMO
        string? page = intent?.GetStringExtra("replio_demo");
        if (!string.IsNullOrEmpty(page))
        {
            Views.MainView.RequestDemo(page);
        }
#endif
    }

    private void HandleShared(Intent? intent)
    {
        if (intent?.Action is not (Intent.ActionSend or Intent.ActionSendMultiple))
        {
            return;
        }

        var uris = new List<global::Android.Net.Uri>();
#pragma warning disable CA1422 // The typed GetParcelableExtra exists only since Android 13.
        if (intent.Action == Intent.ActionSend && intent.GetParcelableExtra(Intent.ExtraStream) is global::Android.Net.Uri one)
        {
            uris.Add(one);
        }
        else if (intent.GetParcelableArrayListExtra(Intent.ExtraStream) is { } many)
        {
            uris.AddRange(many.OfType<global::Android.Net.Uri>());
        }
#pragma warning restore CA1422

        if (uris.Count > 0)
        {
            CallAssistant.Instance.ImportShared(uris);
        }

        intent.SetAction(Intent.ActionMain);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
        IsInForeground = true;
        if (_returnToCall)
        {
            // Woken by a tap on the call: once the screen exists (and nothing needs a permission answer), back to the call.
            _returnToCall = false;
            Window?.DecorView?.PostDelayed(() =>
            {
                if (HasMicrophonePermission && !IsFinishing)
                {
                    MoveTaskToBack(true);
                }
            }, 1200);
        }

        // Back from the system settings (accessibility, battery): the setup card shows what is on now.
        CallAssistant.Instance.NotifyChanged();
        CallAssistant.Instance.ImportPhoneRecordings();
    }

    protected override void OnPause()
    {
        IsInForeground = false;
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        base.OnDestroy();
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

    /// <summary>Asks for one more permission (the phone's call recordings); true when granted.</summary>
    public Task<bool> RequestPermissionAsync(string permission)
    {
        if (CheckSelfPermission(permission) == Permission.Granted)
        {
            return Task.FromResult(true);
        }

        _otherRequest?.TrySetResult(false);
        _otherRequest = new TaskCompletionSource<bool>();
        _otherPermission = permission;
        RequestPermissions([permission], OtherPermissionRequestCode);
        return _otherRequest.Task;
    }

    private const int OtherPermissionRequestCode = 4103;
    private TaskCompletionSource<bool>? _otherRequest;
    private string _otherPermission = string.Empty;

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == PermissionRequestCode)
        {
            _permissionRequest?.TrySetResult(HasMicrophonePermission);
            _permissionRequest = null;
        }
        else if (requestCode == OtherPermissionRequestCode)
        {
            _otherRequest?.TrySetResult(CheckSelfPermission(_otherPermission) == Permission.Granted);
            _otherRequest = null;
        }

        CallAssistant.Instance.NotifyChanged();
    }

    /// <summary>
    /// Opens a web page inside Replio's task, as a Custom Tab of the browser where it supports them: «Назад» comes
    /// back to Replio, and after signing in Replio returns by itself (<see cref="ReturnFromBrowser"/>). Other
    /// links, and phones without a running Replio screen, go to the browser as before.
    /// </summary>
    public static void OpenLink(Context fallback, string uri)
    {
        if (Current is not { } activity || !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            OpenUri(fallback, uri);
            return;
        }

        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(uri));
            // The Custom Tabs protocol without the AndroidX library: an empty session and the toolbar colour.
            var extras = new Bundle();
            extras.PutBinder("android.support.customtabs.extra.SESSION", null);
            intent.PutExtras(extras);
            intent.PutExtra("android.support.customtabs.extra.TOOLBAR_COLOR", global::Android.Graphics.Color.ParseColor("#0E1116").ToArgb());
            intent.PutExtra("android.support.customtabs.extra.TITLE_VISIBILITY", 1);
            activity.StartActivity(intent);
        }
        catch (Exception)
        {
            OpenUri(fallback, uri);
        }
    }

    /// <summary>Signed in on the site: Replio comes back over the page (it was opened in Replio's own task).</summary>
    public void ReturnFromBrowser()
    {
        if (IsInForeground)
        {
            return;
        }

        try
        {
            var intent = new Intent(this, typeof(MainActivity));
            intent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront);
            StartActivity(intent);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[account] Could not come back from the browser: " + ex.Message);
        }
    }

    /// <summary>Opens a link or a file outside the app (a browser, a player).</summary>
    public static void OpenUri(Context context, string uri)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(uri));
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception)
        {
            // No app for this link.
        }
    }

    /// <summary>
    /// FLAG_SECURE: Android shows a black screen instead of Replio in screen recordings, casting and screenshots,
    /// the phone counterpart of hiding the windows from screen sharing on the computer.
    /// </summary>
    public void SetHiddenFromScreenCapture(bool hidden) => RunOnUiThread(() =>
    {
        if (hidden)
        {
            Window?.AddFlags(global::Android.Views.WindowManagerFlags.Secure);
        }
        else
        {
            Window?.ClearFlags(global::Android.Views.WindowManagerFlags.Secure);
        }
    });

    /// <summary>Opens a file of the app (an attached document, a recording) through FileProvider in the app that handles its type.</summary>
    public bool OpenFile(string path)
    {
        try
        {
            var file = new Java.IO.File(path);
            global::Android.Net.Uri uri = AndroidX.Core.Content.FileProvider.GetUriForFile(this, PackageName + ".files", file)!;
            string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            string type = global::Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension) ?? "*/*";
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, type);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            StartActivity(Intent.CreateChooser(intent, file.Name));
            return true;
        }
        catch (Exception)
        {
            return false;
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
