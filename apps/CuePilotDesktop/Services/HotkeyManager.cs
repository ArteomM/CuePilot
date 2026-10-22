using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CuePilotDesktop.Services;

public class HotkeyManager
{
    private readonly IntPtr _windowHandle;
    private readonly List<int> _registeredIds = [];

    public HotkeyManager(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
    }

    public bool Register(int id, HotkeyGesture gesture) => Register(id, gesture, out _);

    /// <param name="error">Why Windows refused, e.g. «Hot key is already registered» when another program holds it.</param>
    public bool Register(int id, HotkeyGesture gesture, out string error)
    {
        bool ok = RegisterHotKey(_windowHandle, id, gesture.Modifiers, gesture.VirtualKey);
        if (ok)
        {
            _registeredIds.Add(id);
            error = string.Empty;
        }
        else
        {
            int code = Marshal.GetLastWin32Error();
            error = $"{code}: {new Win32Exception(code).Message}";
        }

        return ok;
    }

    public void UnregisterAll()
    {
        foreach (int id in _registeredIds)
        {
            UnregisterHotKey(_windowHandle, id);
        }

        _registeredIds.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
