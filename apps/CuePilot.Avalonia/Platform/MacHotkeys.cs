using System.Runtime.InteropServices;
using Avalonia.Input;

namespace CuePilot.Avalonia.Platform;

/// <summary>A hotkey text from settings ("Ctrl+Shift+/") turned into a macOS key code and Carbon modifiers.</summary>
public sealed class MacHotkeyGesture
{
    // Carbon modifier flags. «Ctrl» stays the Control key (⌃) rather than ⌘, so the defaults (Ctrl+R, Ctrl+Enter)
    // do not take over everyday Mac shortcuts like ⌘R; «Win» is ⌘ and «Alt» is ⌥ Option.
    private const uint CmdKey = 0x0100;
    private const uint ShiftKey = 0x0200;
    private const uint OptionKey = 0x0800;
    private const uint ControlKey = 0x1000;

    public uint KeyCode { get; private init; }
    public uint Modifiers { get; private init; }
    public string DisplayText { get; private init; } = string.Empty;

    public static bool TryParse(string? input, out MacHotkeyGesture? gesture, out string error)
    {
        gesture = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Hotkey is empty.";
            return false;
        }

        string[] tokens = input.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
        {
            error = "Hotkey must include modifiers and key.";
            return false;
        }

        uint modifiers = 0;
        string? keyToken = null;
        var parts = new List<string>();
        foreach (string token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ControlKey;
                    parts.Add("Ctrl");
                    continue;
                case "shift":
                    modifiers |= ShiftKey;
                    parts.Add("Shift");
                    continue;
                case "alt" or "option":
                    modifiers |= OptionKey;
                    parts.Add("Alt");
                    continue;
                case "win" or "windows" or "cmd" or "command":
                    modifiers |= CmdKey;
                    parts.Add("Win");
                    continue;
            }

            keyToken = token;
        }

        if (modifiers == 0)
        {
            error = "Hotkey must include at least one modifier.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(keyToken))
        {
            error = "Hotkey must include a key.";
            return false;
        }

        keyToken = keyToken switch
        {
            "\\" => "Oem5",
            "/" => "Oem2",
            _ => keyToken
        };

        if (!Enum.TryParse(keyToken, true, out Key key) || !KeyCodes.TryGetValue(key, out uint keyCode))
        {
            error = $"Unknown key '{keyToken}'.";
            return false;
        }

        parts.Add(key switch
        {
            Key.Oem5 => "\\",
            Key.Oem2 => "/",
            _ => key.ToString().ToUpperInvariant()
        });
        gesture = new MacHotkeyGesture { KeyCode = keyCode, Modifiers = modifiers, DisplayText = string.Join('+', parts) };
        return true;
    }

    /// <summary>Avalonia keys (same names as the WPF ones the settings use) → macOS virtual key codes (US layout positions).</summary>
    private static readonly Dictionary<Key, uint> KeyCodes = new()
    {
        [Key.A] = 0x00, [Key.S] = 0x01, [Key.D] = 0x02, [Key.F] = 0x03, [Key.H] = 0x04, [Key.G] = 0x05,
        [Key.Z] = 0x06, [Key.X] = 0x07, [Key.C] = 0x08, [Key.V] = 0x09, [Key.B] = 0x0B, [Key.Q] = 0x0C,
        [Key.W] = 0x0D, [Key.E] = 0x0E, [Key.R] = 0x0F, [Key.Y] = 0x10, [Key.T] = 0x11, [Key.O] = 0x1F,
        [Key.U] = 0x20, [Key.I] = 0x22, [Key.P] = 0x23, [Key.L] = 0x25, [Key.J] = 0x26, [Key.K] = 0x28,
        [Key.N] = 0x2D, [Key.M] = 0x2E,
        [Key.D1] = 0x12, [Key.D2] = 0x13, [Key.D3] = 0x14, [Key.D4] = 0x15, [Key.D6] = 0x16, [Key.D5] = 0x17,
        [Key.D9] = 0x19, [Key.D7] = 0x1A, [Key.D8] = 0x1C, [Key.D0] = 0x1D,
        [Key.OemPlus] = 0x18, [Key.OemMinus] = 0x1B, [Key.OemCloseBrackets] = 0x1E, [Key.OemOpenBrackets] = 0x21,
        [Key.OemQuotes] = 0x27, [Key.OemSemicolon] = 0x29, [Key.Oem5] = 0x2A, [Key.OemComma] = 0x2B,
        [Key.Oem2] = 0x2C, [Key.OemPeriod] = 0x2F, [Key.OemTilde] = 0x32,
        [Key.Enter] = 0x24, [Key.Tab] = 0x30, [Key.Space] = 0x31, [Key.Back] = 0x33, [Key.Escape] = 0x35,
        [Key.Delete] = 0x75, [Key.Home] = 0x73, [Key.End] = 0x77, [Key.PageUp] = 0x74, [Key.PageDown] = 0x79,
        [Key.Left] = 0x7B, [Key.Right] = 0x7C, [Key.Down] = 0x7D, [Key.Up] = 0x7E,
        [Key.F1] = 0x7A, [Key.F2] = 0x78, [Key.F3] = 0x63, [Key.F4] = 0x76, [Key.F5] = 0x60, [Key.F6] = 0x61,
        [Key.F7] = 0x62, [Key.F8] = 0x64, [Key.F9] = 0x65, [Key.F10] = 0x6D, [Key.F11] = 0x67, [Key.F12] = 0x6F
    };
}

/// <summary>
/// System-wide hotkeys on macOS through Carbon RegisterEventHotKey: works without the Accessibility permission
/// and fires on the main thread (Avalonia's UI thread), like WM_HOTKEY on Windows.
/// </summary>
public sealed class MacHotkeyManager : IDisposable
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint EventClassKeyboard = 0x6B657962; // 'keyb'
    private const uint EventHotKeyPressed = 5;
    private const uint EventParamDirectObject = 0x2D2D2D2D; // '----'
    private const uint TypeEventHotKeyId = 0x686B6964; // 'hkid'
    private const uint Signature = 0x43554550; // 'CUEP'

    private readonly Dictionary<int, IntPtr> _registered = [];
    private readonly Action<int> _onHotkey;
    private readonly EventHandlerProc _handler;
    private IntPtr _handlerRef;

    public MacHotkeyManager(Action<int> onHotkey)
    {
        _onHotkey = onHotkey;
        _handler = OnEvent; // kept in a field so the GC does not collect the callback
        var spec = new[] { new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed } };
        int status = InstallEventHandler(GetApplicationEventTarget(), _handler, 1, spec, IntPtr.Zero, out _handlerRef);
        if (status != 0)
        {
            throw new InvalidOperationException($"InstallEventHandler failed: {status}");
        }
    }

    public bool Register(int id, MacHotkeyGesture gesture, out string error)
    {
        error = string.Empty;
        var hotkeyId = new EventHotKeyId { Signature = Signature, Id = (uint)id };
        int status = RegisterEventHotKey(gesture.KeyCode, gesture.Modifiers, hotkeyId, GetApplicationEventTarget(), 0, out IntPtr hotKeyRef);
        if (status != 0)
        {
            // -9878 eventHotKeyExistsErr: another app already owns this combination.
            error = status == -9878 ? "the combination is taken by another app" : $"RegisterEventHotKey failed: {status}";
            return false;
        }

        _registered[id] = hotKeyRef;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (IntPtr hotKeyRef in _registered.Values)
        {
            UnregisterEventHotKey(hotKeyRef);
        }

        _registered.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_handlerRef != IntPtr.Zero)
        {
            RemoveEventHandler(_handlerRef);
            _handlerRef = IntPtr.Zero;
        }
    }

    private int OnEvent(IntPtr callRef, IntPtr eventRef, IntPtr userData)
    {
        int status = GetEventParameter(eventRef, EventParamDirectObject, TypeEventHotKeyId, IntPtr.Zero,
            (uint)Marshal.SizeOf<EventHotKeyId>(), IntPtr.Zero, out EventHotKeyId hotkeyId);
        if (status == 0 && hotkeyId.Signature == Signature)
        {
            _onHotkey((int)hotkeyId.Id);
        }

        return 0;
    }

    private delegate int EventHandlerProc(IntPtr callRef, IntPtr eventRef, IntPtr userData);

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [DllImport(Carbon)]
    private static extern IntPtr GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(IntPtr target, EventHandlerProc handler, uint numTypes, EventTypeSpec[] list, IntPtr userData, out IntPtr handlerRef);

    [DllImport(Carbon)]
    private static extern int RemoveEventHandler(IntPtr handlerRef);

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id, IntPtr target, uint options, out IntPtr hotKeyRef);

    [DllImport(Carbon)]
    private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(Carbon)]
    private static extern int GetEventParameter(IntPtr eventRef, uint name, uint desiredType, IntPtr actualType, uint bufferSize, IntPtr actualSize, out EventHotKeyId data);
}
