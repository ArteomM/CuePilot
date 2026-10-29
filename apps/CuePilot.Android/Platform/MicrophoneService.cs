using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace CuePilot.Android.Platform;

/// <summary>
/// Foreground service while CuePilot records: Android keeps the microphone open with the screen off or another
/// app in front only for a visible «microphone» service with its notification.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMicrophone)]
public class MicrophoneService : Service
{
    private const string ChannelId = "cuepilot-recording";
    private const int NotificationId = 4102;
    private PowerManager.WakeLock? _wakeLock;

    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(MicrophoneService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    public static void Stop(Context context) => context.StopService(new Intent(context, typeof(MicrophoneService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        Notification notification = BuildNotification();
        // The «microphone» service type exists since Android 11.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeMicrophone);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }

        if (_wakeLock == null && GetSystemService(PowerService) is PowerManager power)
        {
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "CuePilot:recording");
            _wakeLock.Acquire();
        }

        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }

        _wakeLock = null;
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        string title = MobileText.Get("RecordingNotificationTitle");
        string text = MobileText.Get("RecordingNotificationText");
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && manager?.GetNotificationChannel(ChannelId) == null)
        {
            manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, MobileText.Get("RecordingChannelName"), NotificationImportance.Low));
        }

        Intent? open = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty);
        PendingIntent? content = open == null
            ? null
            : PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

#pragma warning disable CA1416, CA1422 // Notification.Builder(context, channel) exists from API 26, the minimum of this app.
        var builder = new Notification.Builder(this, ChannelId)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetSmallIcon(Resource.Drawable.ic_stat_mic)
            .SetOngoing(true);
#pragma warning restore CA1416, CA1422
        if (content != null)
        {
            builder.SetContentIntent(content);
        }

        return builder.Build();
    }
}
