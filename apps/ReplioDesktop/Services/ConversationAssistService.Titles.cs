using ReplioDesktop.Models;

namespace ReplioDesktop.Services;

public partial class ConversationAssistService
{
    private const int MaxTitleSummaryChars = 6000;

    /// <summary>
    /// A short, clear name for a session nobody named, made by the fast model from the finished summary.
    /// The topic prefix is added by <see cref="SessionTitles.Compose"/>, so the model is told not to repeat it.
    /// </summary>
    public async Task<AssistResult> BuildSessionTitleAsync(
        SessionSummary session,
        string summary,
        string topicName,
        AssistOptions options,
        CancellationToken cancellationToken = default)
    {
        string text = SessionScreenshots.StripLinks(summary);
        if (text.Length > MaxTitleSummaryChars)
        {
            text = text[..MaxTitleSummaryChars];
        }

        string systemPrompt =
            "You name meetings for a meeting history list. Answer with the name only: one line, Russian language, " +
            "no quotes, no Markdown, no final period.";
        string topicNote = string.IsNullOrWhiteSpace(topicName)
            ? string.Empty
            : $"The meeting belongs to the topic «{topicName.Trim()}». The app puts the topic name in front of the title itself, so do not repeat it.\n";
        string userPrompt =
            "Give this meeting a short but clear title (3 to 7 words, at most 60 characters) based on its summary below. " +
            "Name what the meeting was about or what it decided, the way a colleague would find it in a list later. " +
            "Keep proper nouns, product names, companies and terms exactly as in the summary. " +
            "No dates, no words like «Встреча», «Обсуждение», «Созвон» at the start unless the title makes no sense without them.\n" +
            "Write in Russian.\n" +
            topicNote +
            MeetingTypeLine(options) + "\n" +
            "Summary:\n" + text;

        LlmResponse response = await CompleteAsync(options, AssistModelTier.Fast, systemPrompt, userPrompt, [], cancellationToken);
        return AssistResult.From(response, SessionTitles.Clean(response.Text));
    }
}
