using System.Text;

namespace ReplioDesktop.Services;

/// <summary>One earlier session of the topic, as the live hints see it.</summary>
public sealed record TopicSessionDigest(string Title, DateTime StartedAt, string Summary);

/// <summary>
/// Background for the live hints («Что сказать», «Что ответить», «Что спросить», follow-up questions, recap):
/// the topic «Сводка» and the summaries of the topic's other sessions, newest first, within a size budget,
/// so a hint knows what earlier meetings on the same topic decided and left open.
/// </summary>
public static class TopicHintContext
{
    private const int MaxTotalChars = 18000;
    private const int MaxTopicSummaryChars = 5000;
    private const int MinPerSessionChars = 700;
    private const int MaxPerSessionChars = 3000;

    public static string Build(string topicName, string topicSummary, IReadOnlyList<TopicSessionDigest> sessions, bool english)
    {
        string overview = Clean(topicSummary);
        List<TopicSessionDigest> withSummary = sessions
            .Select(s => s with { Summary = Clean(s.Summary) })
            .Where(s => s.Summary.Length > 0)
            .OrderByDescending(s => s.StartedAt)
            .ToList();
        if (overview.Length == 0 && withSummary.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.Append(english ? "Topic: " : "Тема: ").AppendLine(topicName.Trim());
        if (overview.Length > 0)
        {
            text.AppendLine()
                .AppendLine(english ? "Topic overview (all its meetings and documents):" : "Сводка по теме (все встречи и документы):")
                .AppendLine(Limit(overview, MaxTopicSummaryChars));
        }

        if (withSummary.Count > 0)
        {
            text.AppendLine()
                .AppendLine(english ? "Summaries of earlier meetings on this topic, newest first:" : "Итоги прошлых встреч по теме, новые первыми:");
            int perSession = Math.Clamp((MaxTotalChars - text.Length) / withSummary.Count, MinPerSessionChars, MaxPerSessionChars);
            var skipped = new List<TopicSessionDigest>();
            foreach (TopicSessionDigest session in withSummary)
            {
                if (text.Length + Math.Min(session.Summary.Length, perSession) > MaxTotalChars)
                {
                    skipped.Add(session);
                    continue;
                }

                text.AppendLine()
                    .Append("### ").Append(session.Title.Trim()).Append(" · ").AppendLine(session.StartedAt.ToString("dd.MM.yyyy HH:mm"))
                    .AppendLine(Limit(session.Summary, perSession));
            }

            if (skipped.Count > 0)
            {
                // Older meetings that did not fit: at least their names and dates, so the model knows they exist.
                text.AppendLine()
                    .AppendLine(english ? "Older meetings (summaries left out for size):" : "Более ранние встречи (итоги не поместились):");
                foreach (TopicSessionDigest session in skipped.Take(30))
                {
                    text.Append("- ").Append(session.Title.Trim()).Append(" · ").AppendLine(session.StartedAt.ToString("dd.MM.yyyy"));
                }
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The prompt block for a hint, or nothing when the session has no topic.</summary>
    public static string PromptBlock(string topicContext) =>
        string.IsNullOrWhiteSpace(topicContext)
            ? string.Empty
            : "Topic context: the current meeting belongs to a topic; below are the topic overview and summaries of its earlier meetings. " +
              "Use them for background (people, decisions, open tasks, figures) and to avoid asking what is already settled. " +
              "The current transcript wins when they disagree.\n" + topicContext + "\n\n";

    // Links to screenshots and files mean nothing to the model: their words are kept, the addresses dropped.
    private static string Clean(string text) => SessionScreenshots.StripLinks(text);

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        int cut = text.LastIndexOf('\n', maxChars);
        return (cut > maxChars / 2 ? text[..cut] : text[..maxChars]).TrimEnd() + " …";
    }
}
