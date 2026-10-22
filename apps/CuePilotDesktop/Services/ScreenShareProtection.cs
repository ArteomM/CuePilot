using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CuePilotDesktop.Services;

/// <summary>
/// Hides CuePilot windows from screen sharing, recording and screenshots while keeping them visible locally
/// (AppSettings.ShowInScreenShare = false). Uses SetWindowDisplayAffinity: WDA_EXCLUDEFROMCAPTURE on
/// Windows 10 2004+, otherwise WDA_MONITOR (the window shows up as a black rectangle instead).
/// </summary>
public static class ScreenShareProtection
{
    private const uint WdaNone = 0x00;
    private const uint WdaMonitor = 0x01;
    private const uint WdaExcludeFromCapture = 0x11;

    private static bool _showInScreenShare;
    private static bool _classHandlerRegistered;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /// <summary>
    /// Remembers the setting and covers every top-level HWND that gets loaded later: dialogs, but also
    /// popups, tooltips, context menus and ComboBox dropdowns, which WPF shows in separate windows.
    /// </summary>
    public static void Initialize(bool showInScreenShare)
    {
        _showInScreenShare = showInScreenShare;
        if (_classHandlerRegistered)
        {
            return;
        }

        _classHandlerRegistered = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnElementLoaded));
    }

    /// <summary>Applies the setting as soon as the window gets its handle, before it is first shown.</summary>
    public static void Track(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply(window);
            return;
        }

        window.SourceInitialized += (_, _) => Apply(window);
    }

    public static void SetShowInScreenShare(bool showInScreenShare)
    {
        _showInScreenShare = showInScreenShare;
        if (Application.Current == null)
        {
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            Apply(window);
        }
    }

    public static void Apply(Window window)
    {
        Apply(new WindowInteropHelper(window).Handle);
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        // Only the root element of an HWND matters (a Window or the root of a popup).
        if (sender is FrameworkElement element
            && PresentationSource.FromVisual(element) is HwndSource source
            && ReferenceEquals(source.RootVisual, element))
        {
            Apply(source.Handle);
        }
    }

    private static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_showInScreenShare)
        {
            SetWindowDisplayAffinity(hwnd, WdaNone);
            return;
        }

        if (SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
        {
            return;
        }

        // Older than Windows 10 2004: others see a black rectangle instead of the window.
        int error = Marshal.GetLastWin32Error();
        bool fallback = SetWindowDisplayAffinity(hwnd, WdaMonitor);
        Trace.WriteLine($"[screen-share] WDA_EXCLUDEFROMCAPTURE failed (error {error}); WDA_MONITOR fallback {(fallback ? "applied" : "failed")}.");
    }
}
