using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CuePilot.Avalonia.Platform;

/// <summary>
/// Hides CuePilot windows from screen sharing, recording and screenshots while keeping them visible locally
/// (AppSettings.ShowInScreenShare = false), like SetWindowDisplayAffinity on Windows: NSWindow.sharingType = none.
/// Popups, menus and tooltips draw inside their window (OverlayPopups in Program.cs), so they are covered too.
/// </summary>
public static class MacScreenShareProtection
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const long SharingNone = 0;
    private const long SharingReadOnly = 1;

    private static bool _showInScreenShare;
    private static bool _classHandlerRegistered;

    public static void Initialize(bool showInScreenShare)
    {
        _showInScreenShare = showInScreenShare;
        if (_classHandlerRegistered || !OperatingSystem.IsMacOS())
        {
            return;
        }

        _classHandlerRegistered = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Apply(window), RoutingStrategies.Direct);
    }

    public static void SetShowInScreenShare(bool showInScreenShare, IEnumerable<Window> windows)
    {
        _showInScreenShare = showInScreenShare;
        foreach (Window window in windows)
        {
            Apply(window);
        }
    }

    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle() is not { } handle)
        {
            return;
        }

        IntPtr nsWindow = handle is global::Avalonia.Platform.IMacOSTopLevelPlatformHandle mac ? mac.NSWindow : IntPtr.Zero;
        if (nsWindow == IntPtr.Zero)
        {
            return;
        }

        try
        {
            objc_msgSend(nsWindow, sel_registerName("setSharingType:"), _showInScreenShare ? SharingReadOnly : SharingNone);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("[screen-share] setSharingType failed: " + ex.Message);
        }
    }

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, long value);
}
