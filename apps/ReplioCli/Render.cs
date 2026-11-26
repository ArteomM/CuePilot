using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReplioDesktop.Models;
using ReplioDesktop.Services;

namespace ReplioCli;

/// <summary>Sessions and topics as Markdown for people and agents, and as JSON for scripts.</summary>
public static class Render
{
    public static readonly JsonSerializerOptions JsonOutput = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex SummaryHeading = new(@"^(#{1,5})\s", RegexOptions.Multiline | RegexOptions.Compiled);

    public static string Duration(SessionSummary s)
    {
        DateTime end = s.EndedAt ?? (s.Status == SessionStatus.Active ? DateTime.Now : s.StartedAt);
        double minutes = Math.Max(0, (end - s.StartedAt).TotalMinutes);
        return minutes.ToString("0", CultureInfo.InvariantCulture) + " min";
    }

    public static string When(SessionSummary s) =>
        s.StartedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
        (s.EndedAt is { } end ? "–" + end.ToString(end.Date == s.StartedAt.Date ? "HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : string.Empty);

    public static int SpeechLines(SessionSummary s) => s.Transcript.Count(t => string.IsNullOrEmpty(t.ScreenshotPath));

    public static bool HasSummary(SessionSummary s) => s.SummaryText.Contains("##", StringComparison.Ordinal);

    // ---------- list ----------

    public static string ListText(Archive archive, IReadOnlyList<SessionSummary> sessions)
    {
        if (sessions.Count == 0)
        {
            return "No sessions found.";
        }

        var text = new StringBuilder();
        foreach (SessionSummary s in sessions)
        {
            string topic = archive.TopicName(s);
            text.Append(s.Id[..Math.Min(8, s.Id.Length)]).Append("  ")
                .Append(When(s).PadRight(22)).Append("  ")
                .Append(s.Title);
            if (topic.Length > 0 && !s.Title.StartsWith(topic, StringComparison.CurrentCultureIgnoreCase))
            {
                text.Append("  [").Append(topic).Append(']');
            }

            if (s.Status == SessionStatus.Active)
            {
                text.Append("  (recording)");
            }

            text.AppendLine();
        }

        text.Append(sessions.Count).Append(" session(s). Open one with: replio show <id or title>");
        return text.ToString();
    }

    public static JsonObject SessionHeader(Archive archive, SessionSummary s) => new()
    {
        ["id"] = s.Id,
        ["title"] = s.Title,
        ["topic"] = archive.TopicName(s),
        ["startedAt"] = s.StartedAt.ToString("s", CultureInfo.InvariantCulture),
        ["endedAt"] = s.EndedAt?.ToString("s", CultureInfo.InvariantCulture),
        ["status"] = s.Status == SessionStatus.Active ? "recording" : "completed",
        ["durationMinutes"] = Math.Round(((s.EndedAt ?? DateTime.Now) - s.StartedAt).TotalMinutes, 1),
        ["transcriptLines"] = SpeechLines(s),
        ["screenshots"] = s.Screenshots.Count,
        ["hasSummary"] = HasSummary(s),
        ["file"] = archive.SessionFile(s)
    };

    public static string ListJson(Archive archive, IReadOnlyList<SessionSummary> sessions) =>
        new JsonArray(sessions.Select(s => (JsonNode)SessionHeader(archive, s)).ToArray()).ToJsonString(JsonOutput);

    public static string CandidatesText(Archive archive, Selection selection) =>
        selection.Problem + Environment.NewLine + ListText(archive, selection.Candidates);

    // ---------- one session ----------

    /// <summary>
    /// The whole session in one Markdown text. <paramref name="screenshotLink"/> turns a screenshot path into the link
    /// to print: the file itself for «show», «screenshots/…» inside an export.
    /// </summary>
    public static string SessionMarkdown(Archive archive, SessionSummary s, bool withTranscript, Func<string, string> screenshotLink)
    {
        var text = new StringBuilder();
        text.Append("# ").AppendLine(s.Title).AppendLine();
        AppendFacts(text, archive, s);
        text.AppendLine().AppendLine("## Итоги (summary)").AppendLine().AppendLine(SummaryBody(s, screenshotLink, demote: true));

        if (s.Screenshots.Count > 0)
        {
            text.AppendLine().AppendLine("## Снимки экрана (screenshots)").AppendLine();
            foreach (SessionScreenshot shot in s.Screenshots.OrderBy(x => x.Number))
            {
                string label = SessionScreenshots.Label(shot, english: false);
                text.Append(shot.Number).Append(". [").Append(label).Append("](").Append(screenshotLink(shot.FilePath)).AppendLine(")");
            }
        }

        if (withTranscript)
        {
            text.AppendLine().AppendLine("## Транскрипт (transcript)").AppendLine().AppendLine(TranscriptBody(s, screenshotLink));
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string SummaryMarkdown(SessionSummary s, Func<string, string> screenshotLink) =>
        "# " + s.Title + Environment.NewLine + Environment.NewLine + SummaryBody(s, screenshotLink, demote: false) + Environment.NewLine;

    public static string TranscriptMarkdown(SessionSummary s, Func<string, string> screenshotLink) =>
        "# " + s.Title + " — транскрипт" + Environment.NewLine + Environment.NewLine + TranscriptBody(s, screenshotLink) + Environment.NewLine;

    public static JsonObject SessionJson(Archive archive, SessionSummary s, bool withTranscript)
    {
        JsonObject json = SessionHeader(archive, s);
        json["mode"] = s.ModeName;
        json["summary"] = HasSummary(s) ? SessionScreenshots.RemoveSection(s.SummaryText) : string.Empty;
        json["screenshotList"] = new JsonArray(s.Screenshots.OrderBy(x => x.Number).Select(shot => (JsonNode)new JsonObject
        {
            ["number"] = shot.Number,
            ["time"] = shot.Timestamp.ToString("s", CultureInfo.InvariantCulture),
            ["windowTitle"] = shot.WindowTitle,
            ["path"] = shot.FilePath,
            ["exists"] = File.Exists(shot.FilePath)
        }).ToArray());
        if (withTranscript)
        {
            json["transcript"] = new JsonArray(s.Transcript.Select(t => (JsonNode)new JsonObject
            {
                ["time"] = t.Timestamp.ToString("s", CultureInfo.InvariantCulture),
                ["speaker"] = t.SpeakerName,
                ["source"] = t.Source == SessionSource.Mic ? "mic" : "system",
                ["text"] = LineText(t),
                ["screenshot"] = t.ScreenshotPath
            }).ToArray());
        }

        return json;
    }

    // ---------- topics ----------

    public static string TopicsText(Archive archive)
    {
        if (archive.Topics.Count == 0)
        {
            return "No topics yet.";
        }

        var text = new StringBuilder();
        foreach (SessionTopic topic in archive.Topics.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            int sessions = archive.Sessions.Count(s => s.TopicId == topic.Id);
            text.Append(topic.Id[..Math.Min(8, topic.Id.Length)]).Append("  ").Append(topic.Name)
                .Append("  — ").Append(sessions).Append(" session(s), ").Append(topic.Sources.Count).AppendLine(" document(s)");
        }

        return text.ToString().TrimEnd();
    }

    public static string TopicsJson(Archive archive) =>
        new JsonArray(archive.Topics.Select(t => (JsonNode)new JsonObject
        {
            ["id"] = t.Id,
            ["name"] = t.Name,
            ["sessions"] = archive.Sessions.Count(s => s.TopicId == t.Id),
            ["documents"] = t.Sources.Count,
            ["hasOverview"] = !string.IsNullOrWhiteSpace(t.KnowledgeText)
        }).ToArray()).ToJsonString(JsonOutput);

    public static string TopicMarkdown(Archive archive, SessionTopic topic)
    {
        var text = new StringBuilder();
        text.Append("# Группа: ").AppendLine(topic.Name).AppendLine();
        text.Append("- Id: ").AppendLine(topic.Id);
        if (topic.KnowledgeUpdatedAt is { } updated)
        {
            text.Append("- Overview updated: ").AppendLine(updated.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        text.AppendLine().AppendLine("## Сводка (overview)").AppendLine()
            .AppendLine(string.IsNullOrWhiteSpace(topic.KnowledgeText) ? "_No overview yet._" : Demote(SessionScreenshots.StripLinks(topic.KnowledgeText)));

        List<SessionSummary> sessions = archive.Sessions.Where(s => s.TopicId == topic.Id).ToList();
        text.AppendLine().AppendLine("## Сессии (sessions)").AppendLine();
        foreach (SessionSummary s in sessions)
        {
            text.Append("- `").Append(s.Id).Append("` ").Append(When(s)).Append(" — ").AppendLine(s.Title);
        }

        if (sessions.Count == 0)
        {
            text.AppendLine("_None._");
        }

        if (topic.Sources.Count > 0)
        {
            text.AppendLine().AppendLine("## Документы базы знаний (knowledge documents)").AppendLine();
            foreach (KnowledgeSource source in topic.Sources)
            {
                string parsed = Path.Combine(archive.KnowledgeRoot, topic.Id, source.Id + ".md");
                text.Append("- ").Append(source.Title);
                if (!string.IsNullOrWhiteSpace(source.Path))
                {
                    text.Append(" — original: `").Append(source.Path).Append('`');
                }

                text.Append(File.Exists(parsed) ? " — parsed: `" + parsed + "`" : " — not parsed yet").AppendLine();
            }
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    public static JsonObject TopicJson(Archive archive, SessionTopic topic) => new()
    {
        ["id"] = topic.Id,
        ["name"] = topic.Name,
        ["overview"] = SessionScreenshots.StripLinks(topic.KnowledgeText),
        ["sessions"] = new JsonArray(archive.Sessions.Where(s => s.TopicId == topic.Id).Select(s => (JsonNode)SessionHeader(archive, s)).ToArray()),
        ["documents"] = new JsonArray(topic.Sources.Select(source =>
        {
            string parsed = Path.Combine(archive.KnowledgeRoot, topic.Id, source.Id + ".md");
            return (JsonNode)new JsonObject
            {
                ["title"] = source.Title,
                ["kind"] = source.Kind.ToString(),
                ["original"] = source.Path,
                ["parsed"] = File.Exists(parsed) ? parsed : null
            };
        }).ToArray())
    };

    // ---------- parts ----------

    private static void AppendFacts(StringBuilder text, Archive archive, SessionSummary s)
    {
        string topic = archive.TopicName(s);
        text.Append("- Id: `").Append(s.Id).AppendLine("`");
        text.Append("- When: ").Append(When(s)).Append(" (").Append(Duration(s)).AppendLine(")");
        if (s.Status == SessionStatus.Active)
        {
            text.AppendLine("- Status: recording in progress");
        }

        if (topic.Length > 0)
        {
            text.Append("- Topic: ").AppendLine(topic);
        }

        if (!string.IsNullOrWhiteSpace(s.ModeName))
        {
            text.Append("- Mode: ").AppendLine(s.ModeName);
        }

        text.Append("- Transcript lines: ").Append(SpeechLines(s)).Append(", screenshots: ").Append(s.Screenshots.Count).AppendLine();
        text.Append("- Session file: `").Append(archive.SessionFile(s)).AppendLine("`");
    }

    private static string SummaryBody(SessionSummary s, Func<string, string> screenshotLink, bool demote)
    {
        if (!HasSummary(s))
        {
            return "_No AI summary for this session._";
        }

        string body = SessionScreenshots.RemoveSection(s.SummaryText);
        foreach (SessionScreenshot shot in s.Screenshots.Where(x => !string.IsNullOrWhiteSpace(x.FilePath)))
        {
            body = body.Replace("(" + SessionScreenshots.FileLink(shot.FilePath) + ")", "(" + screenshotLink(shot.FilePath) + ")");
        }

        return demote ? Demote(body) : body.Trim();
    }

    private static string TranscriptBody(SessionSummary s, Func<string, string> screenshotLink)
    {
        if (s.Transcript.Count == 0)
        {
            return "_Empty._";
        }

        var text = new StringBuilder();
        foreach (SessionTranscriptItem item in s.Transcript)
        {
            text.Append('[').Append(item.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("] ");
            if (!string.IsNullOrEmpty(item.ScreenshotPath))
            {
                text.Append("📷 [").Append(LineText(item)).Append("](").Append(screenshotLink(item.ScreenshotPath)).AppendLine(")");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.SpeakerName))
            {
                text.Append(item.SpeakerName.Trim()).Append(": ");
            }

            text.AppendLine(LineText(item));
        }

        return text.ToString().TrimEnd();
    }

    private static string LineText(SessionTranscriptItem item)
    {
        string text = !string.IsNullOrWhiteSpace(item.Text) ? item.Text
            : !string.IsNullOrWhiteSpace(item.FormattedText) ? item.FormattedText
            : item.RawText;
        return text.Replace("\r", string.Empty).Replace('\n', ' ').Trim();
    }

    // «## Задачи» inside the summary becomes «### Задачи» under the document's own «## Итоги».
    private static string Demote(string markdown) => SummaryHeading.Replace(markdown.Trim(), m => "#" + m.Groups[1].Value + " ");
}
