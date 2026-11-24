using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ReplioDesktop.Models;

namespace ReplioDesktop.Services;

/// <summary>
/// Screenshots of a session in its texts: the «[снимок N]» markers the summary model leaves become links, and the
/// summary ends with a list of every screenshot. A click on a link opens the picture in the default viewer.
/// </summary>
public static class SessionScreenshots
{
    public const string FolderName = "screenshots";
    public const string SummaryHeading = "## Снимки экрана";
    public const string SummaryHeadingEnglish = "## Screenshots";

    // «[снимок 2]», «[Снимок №2]», «[screenshot 2]»; one already turned into a link is followed by «(» and left alone,
    // a number with no such screenshot (the model made it up) is dropped.
    private static readonly Regex Marker = new(@"\[(?:снимок|скриншот|screenshot)\s*(?:№\s*)?(\d{1,3})\](?!\()",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Section = new(@"(?:^|\n)##\s+(?:Снимки экрана|Screenshots)[ \t]*\r?\n(?:(?!##\s).*(?:\r?\n|$))*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FileLinkPattern = new(@"\[([^\]\n]+)\]\(file:///[^)\s]+\)", RegexOptions.Compiled);

    /// <summary>Folder of a session's kept screenshots under the visual-context root.</summary>
    public static string Folder(string storageRoot, string sessionId) => Path.Combine(storageRoot, sessionId, FolderName);

    public static string FileLink(string path) =>
        new Uri(Path.GetFullPath(path)).AbsoluteUri.Replace("(", "%28").Replace(")", "%29");

    /// <summary>«снимок 2 · 14:05:31 — Название окна» without characters that would break a Markdown link.</summary>
    public static string Label(SessionScreenshot shot, bool english, bool withTitle = true)
    {
        var text = new StringBuilder()
            .Append(english ? "Screenshot " : "Снимок ").Append(shot.Number)
            .Append(" · ").Append(shot.Timestamp.ToString("HH:mm:ss"));
        string title = CleanTitle(shot.WindowTitle);
        if (withTitle && title.Length > 0)
        {
            text.Append(" — ").Append(title);
        }

        return text.ToString();
    }

    /// <summary>
    /// The summary with «[снимок N]» turned into links and a closing «Снимки экрана» list. Safe to run again on its
    /// own output: the old list is replaced, links stay as they are.
    /// </summary>
    public static string ApplyToSummary(string summary, IReadOnlyList<SessionScreenshot> shots)
    {
        string text = RemoveSection(summary ?? string.Empty).TrimEnd();
        List<SessionScreenshot> kept = shots.Where(s => !string.IsNullOrWhiteSpace(s.FilePath)).OrderBy(s => s.Number).ToList();
        if (kept.Count == 0)
        {
            return text;
        }

        Dictionary<int, SessionScreenshot> byNumber = kept.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.First());
        text = Marker.Replace(text, match =>
            int.TryParse(match.Groups[1].Value, out int number) && byNumber.TryGetValue(number, out SessionScreenshot? shot)
                ? $"[снимок {number}]({FileLink(shot.FilePath)})"
                : string.Empty);
        text = Regex.Replace(text, @"[ \t]+(\r?\n|$)", "$1");

        var list = new StringBuilder();
        if (text.Length > 0)
        {
            list.AppendLine(text).AppendLine();
        }

        list.AppendLine(SummaryHeading);
        foreach (SessionScreenshot shot in kept)
        {
            list.Append("- [").Append(Label(shot, english: false)).Append("](").Append(FileLink(shot.FilePath)).AppendLine(")");
        }

        return list.ToString().TrimEnd();
    }

    /// <summary>The summary without its «Снимки экрана» list.</summary>
    public static string RemoveSection(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty : Section.Replace(text, "\n").Trim();

    /// <summary>Text for a model: no screenshot list, links to local files reduced to their words (web links stay).</summary>
    public static string StripLinks(string text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : FileLinkPattern.Replace(RemoveSection(text), "$1").Trim();

    /// <summary>The transcript line a screenshot adds: shown with a thumbnail and opened by a click.</summary>
    public static SessionTranscriptItem TranscriptLine(SessionScreenshot shot, string speakerName, bool english)
    {
        string label = Label(shot, english);
        string title = CleanTitle(shot.WindowTitle);
        return new SessionTranscriptItem
        {
            Timestamp = shot.Timestamp,
            Source = SessionSource.Mic,
            SpeakerName = speakerName,
            RawText = label,
            Text = label,
            FormattedText = label,
            // The summary model sees the marker it may put next to a point: «[снимок 2] Снимок экрана: Excel — бюджет.xlsx».
            SummaryReadyText = $"[снимок {shot.Number}] {speakerName}" + (title.Length > 0 ? ": " + title : string.Empty),
            ScreenshotPath = shot.FilePath
        };
    }

    private static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string clean = Regex.Replace(title.Replace('[', '(').Replace(']', ')'), @"[\r\n\t]+", " ");
        clean = Regex.Replace(clean, @"\s{2,}", " ").Trim();
        return clean.Length > 80 ? clean[..80].TrimEnd() + "…" : clean;
    }
}
