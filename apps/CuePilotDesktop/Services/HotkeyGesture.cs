using System.Windows.Input;
using System.Windows.Interop;

namespace CuePilotDesktop.Services;

public class HotkeyGesture
{
    public uint Modifiers { get; init; }
    public uint VirtualKey { get; init; }
    public string DisplayText { get; init; } = string.Empty;

    public static bool TryParse(string? input, out HotkeyGesture? gesture, out string error)
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
        var normalizedParts = new List<string>();

        foreach (string token in tokens)
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Control;
                normalizedParts.Add("Ctrl");
                continue;
            }

            if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Shift;
                normalizedParts.Add("Shift");
                continue;
            }

            if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Alt;
                normalizedParts.Add("Alt");
                continue;
            }

            if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Win;
                normalizedParts.Add("Win");
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

        if (keyToken == "\\")
        {
            keyToken = "Oem5";
        }
        else if (keyToken == "/")
        {
            keyToken = "Oem2";
        }

        if (!Enum.TryParse(keyToken, true, out Key key) || key == Key.None)
        {
            error = $"Unknown key '{keyToken}'.";
            return false;
        }

        int vkInt = KeyInterop.VirtualKeyFromKey(key);
        if (vkInt <= 0)
        {
            error = "Unable to resolve virtual key.";
            return false;
        }

        string normalizedKey = key switch
        {
            Key.Oem5 => "\\",
            Key.Oem2 => "/",
            _ => key.ToString().ToUpperInvariant()
        };
        normalizedParts.Add(normalizedKey);
        gesture = new HotkeyGesture
        {
            Modifiers = modifiers,
            VirtualKey = (uint)vkInt,
            DisplayText = string.Join('+', normalizedParts)
        };
        return true;
    }
}

public static class HotkeyModifiers
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Win = 0x0008;
}
