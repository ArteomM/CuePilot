using System.Text.RegularExpressions;
using CuePilotDesktop.Models;

namespace CuePilotDesktop.Services;

/// <summary>
/// Session names the app gives itself. A session nobody named gets a short name from its summary once the summary is
/// ready; a session of a topic starts with the topic name and a dot: «KEGOC. Сроки поставки трансформаторов».
/// A name the user typed is never touched.
/// </summary>
public static class SessionTitles
{
    public const int MaxCoreLength = 70;

    // «Session 2026-11-17 14:05» (before 1.0.7 and the English default), «Сессия 2026-11-17 14:05», «Untitled session».
    private static readonly Regex DefaultTitle = new(
        @"^\s*(?:(?:Session|Сессия)\s+\d{4}-\d{2}-\d{2}\s+\d{1,2}:\d{2}|Untitled session|Без названия)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LeadingLabel = new(
        @"^\s*(?:#+\s*)?(?:\*\*)?\s*(?:Название(?:\s+(?:встречи|сессии))?|Заголовок|Title|Session title|Meeting title)\s*[:：—-]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Bullet = new(@"^\s*(?:[-*•]|\d+[.)])\s+", RegexOptions.Compiled);

    /// <summary>The name a new recording gets until the summary names it.</summary>
    public static string Default(DateTime startedAt, bool english) =>
        (english ? "Session " : "Сессия ") + startedAt.ToString("yyyy-MM-dd HH:mm");

    public static bool IsDefault(string? title) => string.IsNullOrWhiteSpace(title) || DefaultTitle.IsMatch(title);

    /// <summary>Whether the app may (re)name the session: nobody named it, or the name is still the one the app gave.</summary>
    public static bool IsAutomatic(SessionSummary session, string topicName) =>
        IsDefault(session.Title) ||
        (!string.IsNullOrWhiteSpace(session.AutoTitle) &&
         (session.Title == Compose(topicName, session.AutoTitle) || session.Title == session.AutoTitle || HasOnlyPrefixChanged(session)));

    /// <summary>«Тема. Название», or just the name without a topic. The topic is not repeated when the name starts with it.</summary>
    public static string Compose(string? topicName, string core)
    {
        string name = core.Trim();
        string topic = topicName?.Trim().TrimEnd('.') ?? string.Empty;
        if (topic.Length == 0)
        {
            return Capitalize(name);
        }

        if (name.StartsWith(topic, StringComparison.OrdinalIgnoreCase))
        {
            string rest = name[topic.Length..].TrimStart(' ', '.', ':', '—', '-', '–', ',');
            if (rest.Length > 0 && (name.Length == topic.Length || !char.IsLetterOrDigit(name[topic.Length])))
            {
                name = rest;
            }
        }

        return name.Length == 0 ? topic : $"{topic}. {Capitalize(name)}";
    }

    /// <summary>The model's answer as a name: one line, no quotes, labels, Markdown or final dot, at most ~70 characters.</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string line = raw.Replace("\r", string.Empty).Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal)) ?? string.Empty;
        line = LeadingLabel.Replace(line, string.Empty);
        line = Bullet.Replace(line, string.Empty);
        line = line.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty).TrimStart('#', ' ');
        line = Regex.Replace(line, @"\s{2,}", " ");
        string previous;
        do
        {
            previous = line;
            line = line.Trim().Trim('"', '\'', '«', '»', '“', '”', '„', '*', ' ').TrimEnd('.', '。', ';', ',');
        }
        while (line != previous);
        return Shorten(line, MaxCoreLength);
    }

    /// <summary>
    /// A name without a model: the first point of «Ключевые моменты» (its topic before the colon), else the first task.
    /// Empty when the summary has neither.
    /// </summary>
    public static string FromSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        string text = SessionScreenshots.StripLinks(summary).Replace("\r", string.Empty);
        foreach (string heading in new[] { ConversationAssistService.SummaryHighlightsHeading, "## Conversation Highlights", "## Key points", ConversationAssistService.SummaryActionItemsHeading, "## Action Items", "## Tasks" })
        {
            string first = FirstBullet(text, heading);
            if (first.Length == 0 || first.Contains("Нет явных действий", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            first = Regex.Replace(first, @"\[снимок\s*\d+\]|\*\*\(.*?\)\*\*|\(Ответственный:.*?\)", string.Empty, RegexOptions.IgnoreCase).Trim();
            int colon = first.IndexOf(':');
            string name = colon is > 2 and < 70 ? first[..colon] : first;
            string clean = Clean(name);
            if (clean.Length >= 3)
            {
                return clean;
            }
        }

        return string.Empty;
    }

    private static bool HasOnlyPrefixChanged(SessionSummary session)
    {
        // «Старая тема. Название» after the session moved or the topic was renamed: still the app's name.
        string core = session.AutoTitle!.Trim();
        return session.Title.EndsWith(". " + Capitalize(core), StringComparison.Ordinal) ||
               session.Title.EndsWith(". " + core, StringComparison.Ordinal);
    }

    private static string FirstBullet(string text, string heading)
    {
        int at = text.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return string.Empty;
        }

        foreach (string raw in text[(at + heading.Length)..].Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                break;
            }

            if (Bullet.IsMatch(line))
            {
                return Bullet.Replace(line, string.Empty).Trim();
            }
        }

        return string.Empty;
    }

    private static string Shorten(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        int cut = text.LastIndexOf(' ', max);
        return (cut > max / 2 ? text[..cut] : text[..max]).TrimEnd(' ', ',', '.', ':', ';', '—', '-') + "…";
    }

    private static string Capitalize(string text) =>
        text.Length == 0 || !char.IsLower(text[0]) ? text : char.ToUpper(text[0]) + text[1..];
}
