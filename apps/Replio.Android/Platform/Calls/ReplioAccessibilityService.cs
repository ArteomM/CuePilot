using Android.AccessibilityServices;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views.Accessibility;

namespace Replio.Android.Platform.Calls;

/// <summary>
/// «Слушать звонок». Android gives the microphone during a call only to the call app, except to an accessibility
/// service whose window is on top: so the Replio button over a call belongs to this service, and the recording then
/// hears the user (and, on speakerphone, the other side). It also takes the screenshot of «Снимок экрана»
/// (Android 11+) without asking for screen recording each time. It reads nothing on the screen: window changes only
/// give the name of the app in front, for the screenshot caption. Declared in Properties/AndroidManifest.xml.
/// </summary>
[Register("ru.replio.app.ReplioAccessibilityService")]
public class ReplioAccessibilityService : AccessibilityService
{
    public static ReplioAccessibilityService? Instance { get; private set; }

    /// <summary>Label of the app in front («Телефон», «Zoom»), for the screenshot.</summary>
    public static string ForegroundAppName { get; private set; } = string.Empty;

    public static bool IsEnabled(Context context)
    {
        if (Instance != null)
        {
            return true;
        }

        string? enabled = Settings.Secure.GetString(context.ContentResolver, Settings.Secure.EnabledAccessibilityServices);
        return enabled?.Contains(context.PackageName + "/", StringComparison.OrdinalIgnoreCase) == true &&
               enabled.Contains("ReplioAccessibilityService", StringComparison.Ordinal);
    }

    /// <summary>Opens the system list of accessibility services (Android does not allow a direct switch).</summary>
    public static void OpenSettings(Context context)
    {
        var intent = new Intent(Settings.ActionAccessibilitySettings);
        intent.AddFlags(ActivityFlags.NewTask);
        try
        {
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[calls] Accessibility settings could not be opened: " + ex.Message);
        }
    }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Instance = this;
        PhonePlatform.Log("[calls] «Слушать звонок» is on.");
        ReplioService.Start(this);
        CallAssistant.Instance.OnAccessibilityChanged(connected: true);
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        if (e?.EventType != EventTypes.WindowStateChanged || e.PackageName is not { } package)
        {
            return;
        }

        string name = package;
        if (name == PackageName || name == "com.android.systemui")
        {
            return;
        }

        try
        {
            ApplicationInfo? info = PackageManager?.GetApplicationInfo(package, 0);
            if (info != null)
            {
                name = PackageManager!.GetApplicationLabel(info) ?? package;
            }
        }
        catch (Exception)
        {
            // Package hidden from this app: keep its id.
        }

        ForegroundAppName = name;
    }

    public override void OnInterrupt()
    {
    }

    public override bool OnUnbind(Intent? intent)
    {
        Disconnect();
        return base.OnUnbind(intent);
    }

    public override void OnDestroy()
    {
        Disconnect();
        base.OnDestroy();
    }

    private void Disconnect()
    {
        if (Instance == null)
        {
            return;
        }

        Instance = null;
        PhonePlatform.Log("[calls] «Слушать звонок» is off.");
        CallAssistant.Instance.OnAccessibilityChanged(connected: false);
    }

    /// <summary>The screen as it is now (Android 11+), as a JPEG file; null when Android refused.</summary>
    public string? TakeScreenshotTo(string path, TimeSpan timeout)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.R)
        {
            return null;
        }

        var done = new ManualResetEventSlim(false);
        string? result = null;
        var callback = new ScreenshotCallback(screenshot =>
        {
            try
            {
                using Bitmap? hardware = Bitmap.WrapHardwareBuffer(screenshot.HardwareBuffer, screenshot.ColorSpace);
                using Bitmap? bitmap = hardware?.Copy(Bitmap.Config.Argb8888!, false);
                screenshot.HardwareBuffer.Close();
                if (bitmap != null)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                    using FileStream stream = File.Create(path);
                    bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 85, stream);
                    result = path;
                }
            }
            catch (Exception ex)
            {
                PhonePlatform.Log("[calls] Screenshot could not be saved: " + ex.Message);
            }
            finally
            {
                done.Set();
            }
        }, error =>
        {
            PhonePlatform.Log("[calls] Screenshot refused: " + error);
            done.Set();
        });

        new Handler(Looper.MainLooper!).Post(() =>
        {
            try
            {
                TakeScreenshot(0, MainExecutor!, callback);
            }
            catch (Exception ex)
            {
                PhonePlatform.Log("[calls] Screenshot failed: " + ex.Message);
                done.Set();
            }
        });
        done.Wait(timeout);
        return result;
    }

    private sealed class ScreenshotCallback : Java.Lang.Object, ITakeScreenshotCallback
    {
        private readonly Action<ScreenshotResult> _success;
        private readonly Action<int> _failure;

        public ScreenshotCallback(Action<ScreenshotResult> success, Action<int> failure)
        {
            _success = success;
            _failure = failure;
        }

        public void OnSuccess(ScreenshotResult screenshot) => _success(screenshot);

        public void OnFailure(int errorCode) => _failure(errorCode);
    }
}
