namespace ReplioDesktop.Services;

/// <summary>
/// How a hotkey from the settings («Ctrl+Shift+/») is shown to the user. Settings keep the Windows-style text on
/// both systems; on macOS it is shown with the Mac key symbols in the usual order: «⌃⇧/». «Ctrl» is the Control key
/// (⌃), «Alt» is Option (⌥) and «Win» is Command (⌘), the same mapping the Mac hotkeys use.
/// </summary>
public static class HotkeyDisplay
{
    public static string Format(string? gesture) => OperatingSystem.IsMacOS() ? FormatMac(gesture) : (gesture ?? string.Empty).Trim();

    public static string FormatMac(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return string.Empty;
        }

        string[] tokens = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool control = false, option = false, shift = false, command = false;
        var keys = new List<string>();
        foreach (string token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    control = true;
                    break;
                case "alt" or "option":
                    option = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "win" or "windows" or "cmd" or "command":
                    command = true;
                    break;
                default:
                    keys.Add(KeySymbol(token));
                    break;
            }
        }

        // Apple's order: Control, Option, Shift, Command, then the key.
        return (control ? "⌃" : string.Empty) + (option ? "⌥" : string.Empty) + (shift ? "⇧" : string.Empty) +
               (command ? "⌘" : string.Empty) + string.Join("+", keys);
    }

    private static string KeySymbol(string key) => key.ToLowerInvariant() switch
    {
        "enter" or "return" => "↩",
        "up" => "↑",
        "down" => "↓",
        "left" => "←",
        "right" => "→",
        "escape" or "esc" => "⎋",
        "back" or "backspace" => "⌫",
        "delete" or "del" => "⌦",
        "tab" => "⇥",
        "space" => "Space",
        "oem2" => "/",
        "oem5" => "\\",
        _ => key.Length == 1 ? key.ToUpperInvariant() : key
    };
}
