using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CuePilot.Avalonia.Platform;

/// <summary>
/// The two macOS privacy permissions a recording needs: the microphone (your voice) and
/// «Screen &amp; System Audio Recording» (the other side of the call, captured by ScreenCaptureKit).
/// </summary>
public static class MacPermissions
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AvFoundationPath = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    public enum MicrophoneState
    {
        NotDetermined = 0,
        Restricted = 1,
        Denied = 2,
        Authorized = 3
    }

    /// <summary>True once the user allowed it; macOS reports a new grant only after CuePilot restarts.</summary>
    public static bool ScreenCaptureGranted
    {
        get
        {
            try
            {
                return CGPreflightScreenCaptureAccess();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[permissions] screen check failed: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>Shows the system prompt the first time and lists CuePilot in System Settings; returns the current state.</summary>
    public static bool RequestScreenCapture()
    {
        try
        {
            return CGRequestScreenCaptureAccess();
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[permissions] screen request failed: " + ex.Message);
            return false;
        }
    }

    public static MicrophoneState Microphone
    {
        get
        {
            try
            {
                NativeLibrary.Load(AvFoundationPath);
                IntPtr deviceClass = objc_getClass("AVCaptureDevice");
                IntPtr mediaType = objc_msgSend_string(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), "soun"); // AVMediaTypeAudio
                long status = objc_msgSend_long(deviceClass, sel_registerName("authorizationStatusForMediaType:"), mediaType);
                return (MicrophoneState)status;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[permissions] microphone check failed: " + ex.Message);
                return MicrophoneState.Authorized;
            }
        }
    }

    /// <summary>
    /// Starts the microphone prompt through the audio helper (inside CuePilot.app, so macOS asks on behalf of CuePilot).
    /// Returns the helper process, which exits once the user answered, or null when it could not start.
    /// </summary>
    public static Process? StartMicrophoneRequest(string helperPath)
    {
        if (!File.Exists(helperPath))
        {
            return null;
        }

        try
        {
            return Process.Start(new ProcessStartInfo(helperPath, "request-mic") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[permissions] microphone request failed: " + ex.Message);
            return null;
        }
    }

    public static void OpenMicrophoneSettings() =>
        OpenUrl("x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone");

    public static void OpenScreenCaptureSettings() =>
        OpenUrl("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture");

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo("/usr/bin/open", url) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[permissions] cannot open System Settings: " + ex.Message);
        }
    }

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGPreflightScreenCaptureAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRequestScreenCaptureAccess();

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_string(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern long objc_msgSend_long(IntPtr receiver, IntPtr selector, IntPtr argument);
}
