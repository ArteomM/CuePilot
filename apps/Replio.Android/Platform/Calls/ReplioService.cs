using Android.App;
using Android.Content;
using Android.Content.PM;
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

    private const string ChannelId = "replio-background";
    private const int NotificationId = 4102;
    private PowerManager.WakeLock? _wakeLock;
    private bool _microphone;

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

    private void Demote()
    {
        if (!_microphone)
        {
            return;
        }

        _microphone = false;
        ReleaseWakeLock();
        ShowNotification(force: true);
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
            StartForeground(NotificationId, notification, type);
        }
        else if (_microphone && Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeMicrophone);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    private Notification BuildNotification()
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && manager?.GetNotificationChannel(ChannelId) == null)
        {
            var channel = new NotificationChannel(ChannelId, CallText.Get("ChannelName"), NotificationImportance.Low);
            channel.SetShowBadge(false);
            manager?.CreateNotificationChannel(channel);
        }

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
        var builder = new Notification.Builder(this, ChannelId)
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
            builder.AddAction(new Notification.Action.Builder(null, CallText.Get("ActionStop"), ServiceIntent(ActionStop, 1)).Build());
            builder.AddAction(new Notification.Action.Builder(null, CallText.Get("ActionHint"), ServiceIntent(ActionHint, 2)).Build());
        }
        else
        {
            builder.AddAction(new Notification.Action.Builder(null, CallText.Get("ActionRecord"), ServiceIntent(ActionRecord, 3)).Build());
        }
#pragma warning restore CA1416, CA1422
        if (content != null)
        {
            builder.SetContentIntent(content);
        }

        return builder.Build();
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
