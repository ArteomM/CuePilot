using CuePilotDesktop.Models;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.ViewModels;

/// <summary>
/// A session nobody named gets a name from its summary: «KEGOC. Сроки поставки» for a topic session, the name alone
/// otherwise. It runs after the recording stops and after «Обновить итоги»; a name the user typed is kept.
/// </summary>
public partial class MainViewModel
{
    private string NewSessionTitle(DateTime startedAt) => SessionTitles.Default(startedAt, IsEnglishInterface);

    private string TopicNameOf(SessionSummary session) =>
        string.IsNullOrWhiteSpace(session.TopicId)
            ? string.Empty
            : _topics.FirstOrDefault(t => t.Id == session.TopicId)?.Name ?? string.Empty;

    /// <summary>Background: never throws, never blocks the UI; the name appears in the list when ready.</summary>
    private async Task AutoTitleSessionAsync(string sessionId, string summary)
    {
        try
        {
            SessionSummary? session = _sessionLifecycleService.GetById(sessionId);
            if (session == null || !SessionTitles.IsAutomatic(session, TopicNameOf(session)))
            {
                return;
            }

            // Only a real summary (it has headings): not the placeholder or the last transcript lines left when the AI failed.
            string clean = SessionScreenshots.StripLinks(summary);
            if (!clean.Contains("##", StringComparison.Ordinal))
            {
                return;
            }

            string core = string.Empty;
            if (CanGenerateSummaryAutomatically())
            {
                try
                {
                    AssistResult result = await _conversationAssistService.BuildSessionTitleAsync(
                        session, clean, TopicNameOf(session), BuildAssistOptions());
                    RecordAssistUsage(session, L["ActionSessionTitle"], result);
                    core = result.Text;
                }
                catch (Exception ex)
                {
                    AppendLog("[session] Title from summary failed: " + ex.Message);
                }
            }

            if (core.Length == 0)
            {
                // No model or no answer: the first point of the summary still says more than a date.
                core = SessionTitles.FromSummary(clean);
            }

            // The user may have typed a name or moved the session while the model was answering.
            session = _sessionLifecycleService.GetById(sessionId);
            if (core.Length == 0 || session == null || !SessionTitles.IsAutomatic(session, TopicNameOf(session)))
            {
                return;
            }

            string title = SessionTitles.Compose(TopicNameOf(session), core);
            _sessionLifecycleService.SetAutoTitle(sessionId, title, core);
            _sessionLifecycleService.Flush();
            ApplyRenamedSession(sessionId);
            AppendLog("[session] Title made from the summary.");
        }
        catch (Exception ex)
        {
            AppendLog("[session] Title from summary skipped: " + ex.Message);
        }
    }

    /// <summary>After a move to another topic (or a topic rename) the app's name gets the new topic in front.</summary>
    private bool RetitleForTopic(string sessionId, string oldTopicName, string newTopicName)
    {
        SessionSummary? session = _sessionLifecycleService.GetById(sessionId);
        if (session == null || string.IsNullOrWhiteSpace(session.AutoTitle) ||
            session.Title != SessionTitles.Compose(oldTopicName, session.AutoTitle))
        {
            return false;
        }

        string title = SessionTitles.Compose(newTopicName, session.AutoTitle);
        if (title == session.Title)
        {
            return false;
        }

        _sessionLifecycleService.SetAutoTitle(sessionId, title, session.AutoTitle);
        return true;
    }

    private void ApplyRenamedSession(string sessionId)
    {
        SessionSummary? renamed = _sessionLifecycleService.GetById(sessionId);
        if (renamed == null)
        {
            return;
        }

        RefreshSessionList();
        if (SelectedSession?.Id == sessionId && !IsEditingSessionTitle)
        {
            CurrentSessionTitle = renamed.Title;
            EditableSessionTitle = renamed.Title;
        }
    }
}
