namespace ReplioDesktop.Services;

/// <summary>
/// What kind of meetings a topic holds and how Replio helps in them (replaces the former «Режимы»):
/// • «Как помогать на встречах»: guidance added to the live hints (sale: needs, objections, next step…);
/// • «Формат итогов»: sections the summary adds after its usual ones (interview: candidate, risks, recommendation…).
/// Each topic has its own; a meeting without a topic, and a topic left on «Как для встреч без темы», uses the one in
/// Settings. Ready presets fill both texts, and the user may edit them (the preset then becomes «Свой вариант»).
/// Preset texts live in the resx files (MeetingPresetName_/Guidance_/Format_ + id).
/// </summary>
public static class MeetingPresets
{
    /// <summary>Topic only: use the Settings variant for meetings without a topic.</summary>
    public const string Inherit = "inherit";

    /// <summary>Texts written or changed by the user.</summary>
    public const string Custom = "custom";

    public const string General = "general";

    public static readonly string[] Builtin = [General, "sales", "interview", "negotiation", "standup", "consulting"];

    public static string NameKey(string id) => "MeetingPresetName_" + id;

    public static string GuidanceKey(string id) => "MeetingPresetGuidance_" + id;

    public static string FormatKey(string id) => "MeetingPresetFormat_" + id;

    public static bool IsBuiltin(string? id) => id != null && Array.IndexOf(Builtin, id) >= 0;

    /// <summary>Old example texts («Пример: …», «Файл не выбран.») were never the user's own.</summary>
    public static bool IsExampleText(string? text)
    {
        string value = (text ?? string.Empty).Trim();
        return value.Length == 0 ||
               value.StartsWith("Пример", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("Example", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The variant in effect for a meeting.</summary>
public sealed record MeetingStyle(string Preset, string Guidance, string SummaryFormat);
