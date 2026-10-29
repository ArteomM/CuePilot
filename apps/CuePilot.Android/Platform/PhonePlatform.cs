namespace CuePilot.Android.Platform;

/// <summary>What the screen needs from Android; MainActivity fills it in (left empty in headless previews).</summary>
public static class PhonePlatform
{
    public static Func<bool> HasMicrophonePermission { get; set; } = () => true;

    public static Func<Task<bool>> RequestMicrophonePermissionAsync { get; set; } = () => Task.FromResult(true);

    /// <summary>Share sheet: title, text.</summary>
    public static Action<string, string> ShareText { get; set; } = (_, _) => { };
}
