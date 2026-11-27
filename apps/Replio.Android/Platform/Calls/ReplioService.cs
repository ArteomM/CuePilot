using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;

namespace Replio.Android.Platform.Calls;

/// <summary>
/// Replio in the background: a quiet notification «Replio ждёт звонка» keeps the process (and the call watcher)
/// alive; while recording the same service runs with the «microphone» type, which Android needs to keep the
/// microphone open behind another app. Declared in Properties/AndroidManifest.xml (the special-use property cannot
/// be written as an attribute).
/// </summary>
[Register("ru.replio.app.ReplioService")]
public class ReplioService : Service
{
    public const string ActionWatch = "ru.replio.app.WATCH";
    public const string ActionRecord = "ru.replio.app.RECORD";
    public const string ActionStop = "ru.replio.app.STOP";
    public const string ActionHint = "ru.replio.app.HINT";
    public const string ActionMicOn = "ru.replio.app.MIC_ON";
    public const string ActionMicOff = "ru.replio.app.MIC_OFF";
    public const string ActionShowBubble = "ru.replio.app.SHOW_BUBBLE";

    // Three channels, so each can be silenced on its own in the Android settings. The waiting one keeps the id of
    // the old single channel: what the user set for it stays.
    private const string WaitingChannelId = "replio-background";
    private const string RecordingChannelId = "replio-recording";
    private const string ResultsChannelId = "replio-results";
    private const int NotificationId = 4102;
    private const int ResultsNotificationId = 4104;
    private static bool _channelsReady;
    private PowerManager.WakeLock? _wakeLock;
    private bool _microphone;
    private global::Android.Media.Projection.MediaProjection? _projection;

    public static ReplioService? Instance { get; private set; }

    /// <summary>Starts the background service (the app calls it on every start; a second call changes nothing).</summary>
    public static void Start(Context context) => Send(context, ActionWatch);

    /// <summary>Becomes a microphone service, then starts the recording through the view model.</summary>
    public static void BeginRecording(Context context)
    {
        if (Instance is { } service && service.TryPromote())
        {
            CallAssistant.Instance.StartCaptureNow();
            return;
        }

        if (!Send(context, ActionRecord))
        {
            // Android refused to start it from the background: a blink of an invisible screen makes it allowed.
            RecordTrampolineActivity.Launch(context);
        }
    }

    /// <summary>The recording itself asks for the microphone type (a recording started inside the app).</summary>
    public static void SetMicrophone(bool on)
    {
        Context context = global::Android.App.Application.Context;
        if (Instance is { } service)
        {
            if (on)
            {
                service.TryPromote();
            }
            else
            {
                service.Demote();
            }
        }
        else if (on)
        {
            Send(context, ActionMicOn);
        }
    }

    public static void Refresh() => Instance?.ShowNotification();

    /// <summary>«Replio на звонках» is off: no background service and no «Ждёт звонка» notification while nothing records.</summary>
    public static void StopIfIdle() => Instance?.StopWhenIdle();

    private void StopWhenIdle()
    {
        if (_microphone || CallAssistant.Instance.IsRecording || CallAssistant.Instance.WatchCalls)
        {
            return;
        }

        try
        {
            PhonePlatform.Log("[service] Calls are off and nothing records: the background service stops.");
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[service] Could not stop: " + ex.Message);
        }
    }

    private static bool Send(Context context, string action)
    {
        var intent = new Intent(context, typeof(ReplioService));
        intent.SetAction(action);
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }

            return true;
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[service] Could not start: " + ex.Message);
            return false;
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
        CallAssistant.Instance.StartWatching(this);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        string action = intent?.Action ?? ActionWatch;
        // Every start of a foreground service must reach startForeground quickly.
        ShowNotification(force: true);
        switch (action)
        {
            case ActionRecord:
                if (TryPromote())
                {
                    CallAssistant.Instance.StartCaptureNow();
                }

                break;
            case ActionMicOn:
                TryPromote();
                break;
            case ActionMicOff:
                Demote();
                break;
            case ActionStop:
                CallAssistant.Instance.Stop();
                break;
            case ActionHint:
                CallAssistant.Instance.WhatToSay();
                break;
            case ActionShowBubble:
                CallAssistant.Instance.ShowBubbleAgain();
                break;
            case ActionWatch:
                StopWhenIdle();
                break;
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        ReleaseWakeLock();
        Instance = null;
        CallAssistant.Instance.StopWatching();
        base.OnDestroy();
    }

    /// <summary>Runs as a microphone service from now on; false when Android does not allow it now.</summary>
    private bool TryPromote()
    {
        if (_microphone)
        {
            return true;
        }

        try
        {
            _microphone = true;
            ShowNotification(force: true);
            if (_wakeLock == null && GetSystemService(PowerService) is PowerManager power)
            {
                _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "Replio:recording");
                _wakeLock.Acquire();
            }

            return true;
        }
        catch (Exception ex)
        {
            _microphone = false;
            PhonePlatform.Log("[service] Microphone service refused: " + ex.Message);
            ShowNotification(force: true);
            return false;
        }
    }

    /// <summary>
    /// The user allowed capturing the sound of other apps: the service takes the «media projection» type first
    /// (Android 14 demands it before the projection exists), then the sound is mixed into the recording.
    /// </summary>
    public bool EnableProjection(int resultCode, Intent data)
    {
        try
        {
            _projectionAllowed = true;
            ShowNotification(force: true);
            var manager = (global::Android.Media.Projection.MediaProjectionManager?)GetSystemService(MediaProjectionService);
            _projection = manager?.GetMediaProjection(resultCode, data);
            if (_projection == null)
            {
                _projectionAllowed = false;
                return false;
            }

            _projection.RegisterCallback(new ProjectionCallback(this), new Handler(Looper.MainLooper!));
            PhoneAudioSource.SetProjection(_projection);
            return true;
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[service] Sound of other apps refused: " + ex.Message);
            _projectionAllowed = false;
            _projection = null;
            ShowNotification(force: true);
            return false;
        }
    }

    public void DisableProjection()
    {
        global::Android.Media.Projection.MediaProjection? projection = _projection;
        _projection = null;
        _projectionAllowed = false;
        PhoneAudioSource.SetProjection(null);
        try
        {
            projection?.Stop();
        }
        catch (Exception)
        {
            // Already stopped by the system.
        }
    }

    private bool _projectionAllowed;

    private sealed class ProjectionCallback(ReplioService service) : global::Android.Media.Projection.MediaProjection.Callback
    {
        public override void OnStop()
        {
            if (service._projection != null)
            {
                service._projection = null;
                service._projectionAllowed = false;
                PhoneAudioSource.SetProjection(null);
            }
        }
    }

    private void Demote()
    {
        if (!_microphone)
        {
            return;
        }

        DisableProjection();
        _microphone = false;
        ReleaseWakeLock();
        ShowNotification(force: true);
        StopWhenIdle();
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }

        _wakeLock = null;
    }

    private void ShowNotification(bool force = false)
    {
        Notification notification = BuildNotification();
        if (!force)
        {
            (GetSystemService(NotificationService) as NotificationManager)?.Notify(NotificationId, notification);
            return;
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
        {
            ForegroundService type = _microphone
                ? ForegroundService.TypeMicrophone | ForegroundService.TypeSpecialUse
                : ForegroundService.TypeSpecialUse;
            if (_projectionAllowed)
            {
                type |= ForegroundService.TypeMediaProjection;
            }

            StartForeground(NotificationId, notification, type);
        }
        else if ((_microphone || _projectionAllowed) && Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            ForegroundService type = (_microphone ? ForegroundService.TypeMicrophone : 0) |
                                     (_projectionAllowed ? ForegroundService.TypeMediaProjection : 0);
            StartForeground(NotificationId, notification, type);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    /// <summary>«Ожидание звонка», «Запись разговора» (both quiet) and «Итоги встреч» (a normal notification).</summary>
    private static void EnsureChannels(NotificationManager? manager)
    {
        if (_channelsReady || manager == null || Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            return;
        }

        try
        {
            // Creating an existing channel again only renames it; its sound and importance stay as the user set them.
            var waiting = new NotificationChannel(WaitingChannelId, CallText.Get("WaitingChannel"), NotificationImportance.Low);
            waiting.SetShowBadge(false);
            manager.CreateNotificationChannel(waiting);
            var recording = new NotificationChannel(RecordingChannelId, CallText.Get("RecordingChannel"), NotificationImportance.Low);
            recording.SetShowBadge(false);
            manager.CreateNotificationChannel(recording);
            var results = new NotificationChannel(ResultsChannelId, CallText.Get("ResultsChannel"), NotificationImportance.Default);
            results.SetShowBadge(true);
            manager.CreateNotificationChannel(results);
            _channelsReady = true;
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[service] Channels: " + ex.Message);
        }
    }

    private Notification BuildNotification()
    {
        EnsureChannels((NotificationManager?)GetSystemService(NotificationService));
        CallAssistant assistant = CallAssistant.Instance;
        string title;
        string text;
        if (assistant.IsRecording)
        {
            title = CallText.Format("RecordingTitle", assistant.ElapsedText);
            text = assistant.CurrentTopicName;
        }
        else if (assistant.InCall)
        {
            title = CallText.Get("InCallTitle");
            text = CallText.Get("InCallText");
        }
        else
        {
            title = CallText.Get("WaitingTitle");
            text = CallText.Get("WaitingText");
        }

        Intent? open = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty);
        PendingIntent? content = open == null
            ? null
            : PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

#pragma warning disable CA1416, CA1422 // Notification.Builder(context, channel) exists from API 26, the minimum of this app.
        var builder = new Notification.Builder(this, assistant.IsRecording ? RecordingChannelId : WaitingChannelId)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetSmallIcon(assistant.IsRecording ? Resource.Drawable.ic_stat_mic : Resource.Drawable.ic_stat_replio)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetShowWhen(false);
        if (assistant.IsRecording)
        {
            builder.SetUsesChronometer(true);
            builder.SetWhen(new DateTimeOffset(assistant.RecordingStarted).ToUnixTimeMilliseconds());
            builder.SetShowWhen(true);
            builder.AddAction(ActionButton(Resource.Drawable.ic_rp_stop, "ActionStop", ServiceIntent(ActionStop, 1)));
            builder.AddAction(ActionButton(Resource.Drawable.ic_rp_quote, "ActionHint", ServiceIntent(ActionHint, 2)));
        }
        else
        {
            builder.AddAction(ActionButton(Resource.Drawable.ic_rp_mic, "ActionRecord", ServiceIntent(ActionRecord, 3)));
        }

        if (assistant.BubbleHidden && (assistant.InCall || assistant.IsRecording))
        {
            builder.AddAction(ActionButton(Resource.Drawable.ic_rp_open, "ActionShowBubble", ServiceIntent(ActionShowBubble, 4)));
        }
#pragma warning restore CA1416, CA1422
        if (content != null)
        {
            builder.SetContentIntent(content);
        }

        return builder.Build();
    }

    private Notification.Action ActionButton(int icon, string textKey, PendingIntent intent) =>
        new Notification.Action.Builder(Icon.CreateWithResource(this, icon), CallText.Get(textKey), intent).Build()!;

    /// <summary>
    /// «Итоги встречи готовы»: the meeting's name and the start of its summary; a tap opens it in Replio, «Поделиться»
    /// sends the summary to mail or a messenger.
    /// </summary>
    public static void ShowResults(string sessionId, string title, string summary)
    {
        Context context = global::Android.App.Application.Context;
        try
        {
            var manager = (NotificationManager?)context.GetSystemService(NotificationService);
            if (manager == null)
            {
                return;
            }

            EnsureChannels(manager);
            var open = new Intent(context, typeof(MainActivity));
            open.PutExtra(MainActivity.OpenSessionExtra, sessionId);
            open.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
            PendingIntent? openIntent = PendingIntent.GetActivity(context, 5, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            var share = new Intent(Intent.ActionSend);
            share.SetType("text/plain");
            share.PutExtra(Intent.ExtraSubject, title);
            share.PutExtra(Intent.ExtraText, title + "\n\n" + summary.Trim());
            Intent chooser = Intent.CreateChooser(share, CallText.Get("ResultsShare"))!;
            chooser.AddFlags(ActivityFlags.NewTask);
            PendingIntent? shareIntent = PendingIntent.GetActivity(context, 6, chooser, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            string preview = Preview(summary);
#pragma warning disable CA1416, CA1422 // Notification.Builder(context, channel) exists from API 26, the minimum of this app.
            var builder = new Notification.Builder(context, ResultsChannelId)
                .SetContentTitle(CallText.Get("ResultsTitle"))
                .SetContentText(title)
                .SetStyle(new Notification.BigTextStyle().BigText(preview.Length == 0 ? title : title + "\n" + preview))
                .SetSmallIcon(Resource.Drawable.ic_stat_replio)
                .SetAutoCancel(true);
            if (openIntent != null)
            {
                builder.SetContentIntent(openIntent);
                builder.AddAction(new Notification.Action.Builder(Icon.CreateWithResource(context, Resource.Drawable.ic_rp_open), CallText.Get("ResultsOpen"), openIntent).Build());
            }

            if (shareIntent != null)
            {
                builder.AddAction(new Notification.Action.Builder(Icon.CreateWithResource(context, Resource.Drawable.ic_rp_share), CallText.Get("ResultsShare"), shareIntent).Build());
            }
#pragma warning restore CA1416, CA1422

            manager.Notify(ResultsNotificationId, builder.Build());
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[service] Results notification: " + ex.Message);
        }
    }

    /// <summary>The first lines of a summary without its markdown marks, at most about 300 characters.</summary>
    private static string Preview(string summary)
    {
        var lines = new List<string>();
        int length = 0;
        foreach (string raw in summary.Split('\n'))
        {
            string line = raw.Trim().TrimStart('#', '*', '-', '•', ' ').Replace("**", string.Empty).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            lines.Add(line);
            length += line.Length;
            if (length > 300 || lines.Count >= 4)
            {
                break;
            }
        }

        string text = string.Join("\n", lines);
        return text.Length > 320 ? text[..317] + "…" : text;
    }

    private PendingIntent ServiceIntent(string action, int code)
    {
        var intent = new Intent(this, typeof(ReplioService));
        intent.SetAction(action);
        return Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? PendingIntent.GetForegroundService(this, code, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!
            : PendingIntent.GetService(this, code, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }
}

/// <summary>
/// An invisible screen that only starts the recording: Android lets a visible app start a microphone service when it
/// would refuse the same from the background.
/// </summary>
[Activity(
    Theme = "@android:style/Theme.Translucent.NoTitleBar",
    ExcludeFromRecents = true,
    NoHistory = true,
    TaskAffinity = "ru.replio.app.record",
    LaunchMode = LaunchMode.SingleInstance)]
public class RecordTrampolineActivity : Activity
{
    public static void Launch(Context context)
    {
        var intent = new Intent(context, typeof(RecordTrampolineActivity));
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.NoAnimation);
        try
        {
            (ReplioAccessibilityService.Instance as Context ?? context).StartActivity(intent);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[service] Recording could not be started: " + ex.Message);
            CallAssistant.Toast(CallText.Get("NotReady"));
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var intent = new Intent(this, typeof(ReplioService));
        intent.SetAction(ReplioService.ActionRecord);
        try
        {
            StartForegroundService(intent);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[service] Recording could not be started: " + ex.Message);
        }

        Finish();
        OverridePendingTransition(0, 0);
    }
}

/// <summary>
/// Asks Android for the sound of other apps (the system «start recording or casting?» dialog), then hands the
/// permission to the background service. Android asks again for every recording; it cannot be remembered.
/// </summary>
[Activity(
    Theme = "@android:style/Theme.Translucent.NoTitleBar",
    ExcludeFromRecents = true,
    TaskAffinity = "ru.replio.app.projection",
    LaunchMode = LaunchMode.SingleInstance)]
public class PlaybackConsentActivity : Activity
{
    private const int RequestCode = 4110;

    public static void Launch(Context context)
    {
        var intent = new Intent(context, typeof(PlaybackConsentActivity));
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.NoAnimation);
        try
        {
            (ReplioAccessibilityService.Instance as Context ?? context).StartActivity(intent);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[audio] Consent screen could not be opened: " + ex.Message);
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var manager = (global::Android.Media.Projection.MediaProjectionManager?)GetSystemService(MediaProjectionService);
        Intent? request = manager?.CreateScreenCaptureIntent();
        if (request == null)
        {
            Finish();
            return;
        }

        StartActivityForResult(request, RequestCode);
    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == RequestCode && resultCode == Result.Ok && data != null && ReplioService.Instance is { } service)
        {
            bool ok = service.EnableProjection((int)resultCode, data);
            CallAssistant.Toast(CallText.Get(ok ? "PlaybackOn" : "PlaybackFailed"));
            CallAssistant.Instance.NotifyChanged();
        }

        Finish();
        OverridePendingTransition(0, 0);
    }
}
