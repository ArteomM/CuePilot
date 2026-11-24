using ReplioDesktop.Models;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// Live hints of a session that belongs to a topic also see the topic «Сводка» and the summaries of its other sessions.
/// </summary>
public partial class MainViewModel
{
    private string BuildTopicHintContext(SessionSummary session)
    {
        if (string.IsNullOrWhiteSpace(session.TopicId))
        {
            return string.Empty;
        }

        SessionTopic? topic = _topics.FirstOrDefault(t => t.Id == session.TopicId);
        if (topic == null)
        {
            return string.Empty;
        }

        string fallback = L["SessionSummaryFallback"];
        List<TopicSessionDigest> sessions = CompletedSessions
            .Where(s => s.TopicId == topic.Id && s.Id != session.Id && !string.IsNullOrWhiteSpace(s.SummaryText) && s.SummaryText.Trim() != fallback)
            .Select(s => new TopicSessionDigest(s.Title, s.StartedAt, s.SummaryText))
            .ToList();
        string context = TopicHintContext.Build(topic.Name, topic.KnowledgeText, sessions, IsEnglishInterface);
        if (context.Length > 0)
        {
            AppendLog($"[assist] Topic context: {sessions.Count} earlier summary(ies), overview {(string.IsNullOrWhiteSpace(topic.KnowledgeText) ? "no" : "yes")}, {context.Length} chars.");
        }

        return context;
    }
}
