using Android.Content;
using Android.Content.PM;
using Android.Provider;

namespace Replio.Android.Platform;

/// <summary>
/// «Папка данных» on the phone. Android cloud apps (Яндекс Диск, OneDrive) do not keep a folder in sync on the phone
/// the way their computer clients do, so the folder is an ordinary folder in the phone's storage, and a sync app
/// (FolderSync, Autosync) keeps it equal to the cloud folder the computer uses. Replio reads and writes it with plain
/// file access, which Android 11+ grants as «Доступ ко всем файлам».
/// </summary>
public static class PhoneStorage
{
    public static bool HasAllFilesAccess()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return global::Android.OS.Environment.IsExternalStorageManager;
        }

        return global::Android.App.Application.Context.CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted;
    }

    /// <summary>
    /// Android 11+: opens the system page «Доступ ко всем файлам» for Replio and returns false (the user comes back and
    /// taps again). Older phones: the usual permission question.
    /// </summary>
    public static async Task<bool> RequestAllFilesAccessAsync()
    {
        if (HasAllFilesAccess())
        {
            return true;
        }

        Context context = (Context?)MainActivity.Current ?? global::Android.App.Application.Context;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            try
            {
                var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission, global::Android.Net.Uri.Parse("package:" + context.PackageName));
                intent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
            }
            catch (Exception)
            {
                var intent = new Intent(Settings.ActionManageAllFilesAccessPermission);
                intent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
            }

            return false;
        }

        return MainActivity.Current is { } activity &&
               await activity.RequestPermissionAsync(global::Android.Manifest.Permission.WriteExternalStorage);
    }

    /// <summary>
    /// The folder a folder picker returned as a path: «content://com.android.externalstorage.documents/tree/
    /// primary%3AYandexDisk%2FReplio» is «/storage/emulated/0/YandexDisk/Replio». Null for a folder that is not in the
    /// phone's storage (a cloud app's own folder list), which plain file access cannot reach.
    /// </summary>
    public static string? TreeUriToPath(string uriText)
    {
        try
        {
            global::Android.Net.Uri? uri = global::Android.Net.Uri.Parse(uriText);
            if (uri == null || uri.Authority != "com.android.externalstorage.documents")
            {
                return null;
            }

            string? id = DocumentsContract.GetTreeDocumentId(uri);
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            int colon = id.IndexOf(':');
            string volume = colon < 0 ? id : id[..colon];
            string relative = colon < 0 ? string.Empty : id[(colon + 1)..];
#pragma warning disable CA1422 // The path of the phone's own storage; Android still answers it.
            string root = volume.Equals("primary", StringComparison.OrdinalIgnoreCase)
                ? global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0"
                : "/storage/" + volume;
#pragma warning restore CA1422
            return relative.Length == 0 ? root : Path.Combine(root, relative);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[data] Folder link not understood: " + ex.Message);
            return null;
        }
    }

    /// <summary>Starts Replio again (the data folder is read once at start).</summary>
    public static void RestartApp()
    {
        try
        {
            Context context = (Context?)MainActivity.Current ?? global::Android.App.Application.Context;
            Intent? launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? string.Empty);
            if (launch?.Component is { } component && Intent.MakeRestartActivityTask(component) is { } restart)
            {
                context.StartActivity(restart);
            }
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[data] Restart failed: " + ex.Message);
        }

        Java.Lang.JavaSystem.Exit(0);
    }
}
