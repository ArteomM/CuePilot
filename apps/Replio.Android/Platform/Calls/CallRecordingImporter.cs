using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.OS;
using Android.Provider;

namespace Replio.Android.Platform.Calls;

/// <summary>
/// Many phones record calls themselves (Samsung «Recordings/Call», Xiaomi «MIUI/sound_recorder/call_rec», others
/// «CallRecordings»). Such a recording has both sides of the call, which no app can capture: after a call Replio
/// looks for recordings that appeared since the last look, copies them into the app and adds them to the topic,
/// where they are transcribed. Needs the «audio files» permission.
/// </summary>
internal static class CallRecordingImporter
{
    private static readonly string[] CallFolders =
    [
        "recordings/call", "call_rec", "callrecord", "call recordings", "callrecordings", "phonerecord", "record/call",
        "sounds/callrecord", "recordings/callrecordings", "voice recorder/call"
    ];

    private static ISharedPreferences Preferences =>
        global::Android.App.Application.Context.GetSharedPreferences("replio_calls", FileCreationMode.Private)!;

    public static string Permission => Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
        ? global::Android.Manifest.Permission.ReadMediaAudio
        : global::Android.Manifest.Permission.ReadExternalStorage;

    public static bool HasPermission(Context context) =>
        context.CheckSelfPermission(Permission) == global::Android.Content.PM.Permission.Granted;

    /// <summary>Starts looking from now on (older recordings stay out).</summary>
    public static void MarkNow()
    {
        if (Preferences.GetLong("recordings_since", 0) == 0)
        {
            Preferences.Edit()?.PutLong("recordings_since", Java.Lang.JavaSystem.CurrentTimeMillis() / 1000)?.Apply();
        }
    }

    /// <summary>Recordings found since the last look, copied into <paramref name="targetFolder"/>.</summary>
    public static IReadOnlyList<string> CollectNew(Context context, string targetFolder)
    {
        var copied = new List<string>();
        if (!HasPermission(context))
        {
            return copied;
        }

        long since = Preferences.GetLong("recordings_since", 0);
        long now = Java.Lang.JavaSystem.CurrentTimeMillis() / 1000;
        if (since == 0)
        {
            // The first look only marks the moment: old recordings are not pulled in.
            Preferences.Edit()?.PutLong("recordings_since", now)?.Apply();
            return copied;
        }

        // «relative_path» exists since Android 10; before it «_data» holds the full path.
        string[] projection = Build.VERSION.SdkInt >= BuildVersionCodes.Q
            ? ["_id", "_display_name", "relative_path", "date_added"]
            : ["_id", "_display_name", "_data", "date_added"];
        try
        {
            using ICursor? cursor = context.ContentResolver?.Query(
                MediaStore.Audio.Media.ExternalContentUri!,
                projection,
                "date_added > ?",
                [since.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                "date_added ASC");
            long newest = since;
            while (cursor != null && cursor.MoveToNext())
            {
                long id = cursor.GetLong(0);
                string name = cursor.GetString(1) ?? $"call-{id}.m4a";
                string folder = (cursor.GetString(2) ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
                newest = Math.Max(newest, cursor.GetLong(3));
                if (!CallFolders.Any(folder.Contains) && !name.Contains("call", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                global::Android.Net.Uri uri = ContentUris.WithAppendedId(MediaStore.Audio.Media.ExternalContentUri!, id);
                string target = Path.Combine(targetFolder, Sanitize(name));
                Directory.CreateDirectory(targetFolder);
                using (Stream? input = context.ContentResolver!.OpenInputStream(uri))
                {
                    if (input == null)
                    {
                        continue;
                    }

                    using FileStream output = File.Create(target);
                    input.CopyTo(output);
                }

                copied.Add(target);
                PhonePlatform.Log("[calls] Call recording picked up: " + name);
            }

            Preferences.Edit()?.PutLong("recordings_since", Math.Max(newest, since))?.Apply();
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[calls] Call recordings could not be read: " + ex.Message);
        }

        return copied;
    }

    /// <summary>Copies files shared to Replio (content links) into the app.</summary>
    public static IReadOnlyList<string> CopyShared(Context context, IEnumerable<global::Android.Net.Uri> uris, string targetFolder)
    {
        var copied = new List<string>();
        foreach (global::Android.Net.Uri uri in uris)
        {
            try
            {
                string name = QueryName(context, uri) ?? "shared-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".m4a";
                string target = Path.Combine(targetFolder, Sanitize(name));
                Directory.CreateDirectory(targetFolder);
                using Stream? input = context.ContentResolver?.OpenInputStream(uri);
                if (input == null)
                {
                    continue;
                }

                using FileStream output = File.Create(target);
                input.CopyTo(output);
                copied.Add(target);
            }
            catch (Exception ex)
            {
                PhonePlatform.Log("[share] File could not be copied: " + ex.Message);
            }
        }

        return copied;
    }

    private static string? QueryName(Context context, global::Android.Net.Uri uri)
    {
        try
        {
            using ICursor? cursor = context.ContentResolver?.Query(uri, ["_display_name"], null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                return cursor.GetString(0);
            }
        }
        catch (Exception)
        {
            // No name: a generated one is used.
        }

        return null;
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }
}
