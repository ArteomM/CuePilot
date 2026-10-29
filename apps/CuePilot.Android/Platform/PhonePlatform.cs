namespace CuePilot.Android.Platform;

/// <summary>What the screen needs from Android; MainActivity fills it in (left empty in headless previews).</summary>
public static class PhonePlatform
{
    public static Func<bool> HasMicrophonePermission { get; set; } = () => true;

    public static Func<Task<bool>> RequestMicrophonePermissionAsync { get; set; } = () => Task.FromResult(true);

    /// <summary>Share sheet: title, text.</summary>
    public static Action<string, string> ShareText { get; set; } = (_, _) => { };

    /// <summary>True keeps CuePilot out of screen recordings, casting and screenshots (FLAG_SECURE).</summary>
    public static Action<bool> SetHiddenFromScreenCapture { get; set; } = _ => { };

    /// <summary>Opens a file of the app in another app (a viewer, a player); false when none can open it.</summary>
    public static Func<string, bool> OpenFile { get; set; } = _ => false;
}
