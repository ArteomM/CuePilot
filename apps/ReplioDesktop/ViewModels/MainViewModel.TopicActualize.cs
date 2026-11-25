using ReplioDesktop.Models;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// «Актуализировать тему»: one button for the whole topic instead of separate ones for the summary and the graph.
/// It parses new or changed materials, redoes meeting summaries that are out of date (never made by the AI, or made
/// before the topic's «Формат итогов» changed), rebuilds the «Сводка» and then the graph (which reads the summary).
/// Steps that are already up to date are skipped; when everything is up to date, the summary and the graph are rebuilt anyway.
/// A meeting that belongs to a topic has this button instead of «Обновить итоги»: there it also redoes that meeting's summary.
/// </summary>
public partial class MainViewModel
{
    private bool _isTopicActualizing;
    private string _topicActualizeStep = string.Empty;

    public RelayCommand ActualizeTopicCommand { get; private set; } = null!;

    /// <summary>True while any step runs, including a summary or graph rebuild started some other way.</summary>
    public bool IsTopicActualizing => _isTopicActualizing || IsTopicBusy || IsGraphBusy;

    /// <summary>Accent dot on the button: the summary or the graph is behind the topic's sessions and materials.</summary>
    public bool IsTopicOutdated
    {
        get
        {
            SessionTopic? topic = SelectedTopic;
            if (topic == null || !TopicHasMaterial(topic))
            {
                return false;
            }

            return IsTopicKnowledgeStale || string.IsNullOrWhiteSpace(topic.KnowledgeText) || IsGraphStale || !HasGraph || HasUnparsedKnowledge ||
                   OutdatedSessionSummaries(topic).Count > 0;
        }
    }

    public string ActualizeTopicButtonText => IsTopicActualizing
        ? (_topicActualizeStep.Length > 0 ? _topicActualizeStep : L["TopicActualizeBusy"])
        : L["TopicActualize"];

    private bool CanActualizeTopic => SelectedTopic is { } topic && !IsTopicActualizing && TopicHasMaterial(topic);

    public RelayCommand ActualizeSessionTopicCommand { get; private set; } = null!;

    /// <summary>The open meeting belongs to a topic: «Актуализировать тему» replaces «Обновить итоги».</summary>
    public bool IsSelectedSessionInTopic => SessionTopicOf(SelectedSession) != null;

    public bool ShowRegenerateSummary => !IsSelectedSessionInTopic;

    /// <summary>Dot on the session's button: its summary or its topic is out of date.</summary>
    public bool IsSelectedSessionTopicOutdated =>
        SelectedSession is { } session && SessionTopicOf(session) is { } topic &&
        (IsSessionSummaryOutdated(session) || (ReferenceEquals(SelectedTopic, topic) && IsTopicOutdated));

    private SessionTopic? SessionTopicOf(SessionSummary? session) =>
        session == null || string.IsNullOrWhiteSpace(session.TopicId) ? null : _topics.FirstOrDefault(t => t.Id == session.TopicId);

    private void OnSessionTopicActualizeChanged()
    {
        OnPropertyChanged(nameof(IsSelectedSessionInTopic));
        OnPropertyChanged(nameof(ShowRegenerateSummary));
        OnPropertyChanged(nameof(IsSelectedSessionTopicOutdated));
        ActualizeSessionTopicCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>From a meeting: its topic is selected in the list (the steps work on the selected topic), then actualized with this meeting.</summary>
    private async Task ActualizeSessionTopicAsync()
    {
        SessionSummary? session = SelectedSession;
        SessionTopic? topic = SessionTopicOf(session);
        if (session == null || topic == null || IsTopicActualizing)
        {
            return;
        }

        if (!ReferenceEquals(SelectedTopic, topic))
        {
            SelectedTopicItem = TopicItems.FirstOrDefault(item => ReferenceEquals(item.Topic, topic)) ?? SelectedTopicItem;
        }

        await ActualizeTopicAsync(session);
    }

    /// <summary>Redoes one meeting's summary in the topic's current format; false after an error (already shown).</summary>
    private async Task<bool> ResummarizeSessionAsync(SessionSummary session)
    {
        try
        {
            AssistResult result = await _conversationAssistService.BuildSessionSummaryAsync(session, BuildAssistOptions(session: session));
            RecordAssistUsage(session, L["ActionSummary"], result);
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                return true;
            }

            _sessionLifecycleService.UpdateSummary(session.Id, result.Text);
            if (SelectedSession?.Id == session.Id)
            {
                CurrentSessionSummary = BuildSummaryText(session);
            }

            _ = AutoTitleSessionAsync(session.Id, result.Text);
            AppendLog($"[topics] Summary of «{session.Title}» redone in the topic's format.");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("[topics] Redoing a meeting summary failed: " + ex.Message);
            NotificationRequested?.Invoke(L["ErrorTitle"], ex.Message, true);
            return false;
        }
    }

    private void OnTopicActualizeStateChanged()
    {
        OnPropertyChanged(nameof(IsTopicActualizing));
        OnPropertyChanged(nameof(IsTopicOutdated));
        OnPropertyChanged(nameof(ActualizeTopicButtonText));
        ActualizeTopicCommand?.RaiseCanExecuteChanged();
        OnSessionTopicActualizeChanged();
    }

    private void SetActualizeStep(string resourceKey)
    {
        _topicActualizeStep = resourceKey.Length > 0 ? L[resourceKey] : string.Empty;
        OnPropertyChanged(nameof(ActualizeTopicButtonText));
    }

    private async Task ActualizeTopicAsync(SessionSummary? alsoSession = null)
    {
        SessionTopic? topic = SelectedTopic;
        if (topic == null || IsTopicActualizing)
        {
            return;
        }

        if (!TryValidateAssistSettings(out string settingsError))
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], settingsError, true);
            return;
        }

        _isTopicActualizing = true;
        OnTopicActualizeStateChanged();
        try
        {
            bool summaryOutdated = IsTopicKnowledgeStale || string.IsNullOrWhiteSpace(topic.KnowledgeText);
            bool graphOutdated = IsGraphStale || !HasGraph;

            // 1. Materials that are new or changed since their last parse (the topic may be switched meanwhile).
            List<KnowledgeItemViewModel> unparsed = KnowledgeItems.Where(item => item.NeedsParsing && !item.IsBusy).ToList();
            if (unparsed.Count > 0)
            {
                SetActualizeStep("TopicActualizeParsing");
                foreach (KnowledgeItemViewModel item in unparsed)
                {
                    if (!ReferenceEquals(SelectedTopic, topic))
                    {
                        break;
                    }

                    await ParseKnowledgeItemAsync(item);
                }

                // New material changes what the summary and the graph should say.
                summaryOutdated = graphOutdated = true;
            }

            // 2. Meeting summaries: never made by the AI, or made before the topic's «Формат итогов» changed
            //    (plus the meeting the button was pressed in).
            List<SessionSummary> outdated = OutdatedSessionSummaries(topic);
            if (alsoSession != null && alsoSession.Status == SessionStatus.Completed && !outdated.Contains(alsoSession))
            {
                outdated.Insert(0, alsoSession);
            }

            for (int i = 0; i < outdated.Count; i++)
            {
                _topicActualizeStep = string.Format(L["TopicActualizeSessions"], i + 1, outdated.Count);
                OnPropertyChanged(nameof(ActualizeTopicButtonText));
                if (!await ResummarizeSessionAsync(outdated[i]))
                {
                    return;
                }
            }

            if (outdated.Count > 0)
            {
                // The «Сводка» is built from the summaries.
                RefreshTopicStats();
                _completedSessionsView.Refresh();
                summaryOutdated = graphOutdated = true;
            }

            bool rebuildAll = !summaryOutdated && !graphOutdated;

            // 3. «Сводка»: the graph is built from it, so it goes first.
            if (summaryOutdated || rebuildAll)
            {
                SetActualizeStep("TopicActualizeSummary");
                if (!await RefreshTopicKnowledgeAsync(topic))
                {
                    return;
                }
            }

            // 4. Graph.
            SetActualizeStep("TopicActualizeGraph");
            if (await BuildTopicGraphAsync(topic))
            {
                ShowFeedback(L["TopicActualizeDone"]);
                AppendLog("[topics] Topic actualized: summary and graph are up to date.");
            }
        }
        finally
        {
            _isTopicActualizing = false;
            SetActualizeStep(string.Empty);
            OnTopicActualizeStateChanged();
        }
    }
}
