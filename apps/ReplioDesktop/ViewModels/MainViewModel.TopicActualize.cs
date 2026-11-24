using ReplioDesktop.Models;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// «Актуализировать тему»: one button for the whole topic instead of separate ones for the summary and the graph.
/// It parses new or changed materials, rebuilds the «Сводка» and then the graph (which reads the summary).
/// Steps that are already up to date are skipped; when everything is up to date, the summary and the graph are rebuilt anyway.
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

            return IsTopicKnowledgeStale || string.IsNullOrWhiteSpace(topic.KnowledgeText) || IsGraphStale || !HasGraph || HasUnparsedKnowledge;
        }
    }

    public string ActualizeTopicButtonText => IsTopicActualizing
        ? (_topicActualizeStep.Length > 0 ? _topicActualizeStep : L["TopicActualizeBusy"])
        : L["TopicActualize"];

    private bool CanActualizeTopic => SelectedTopic is { } topic && !IsTopicActualizing && TopicHasMaterial(topic);

    private void OnTopicActualizeStateChanged()
    {
        OnPropertyChanged(nameof(IsTopicActualizing));
        OnPropertyChanged(nameof(IsTopicOutdated));
        OnPropertyChanged(nameof(ActualizeTopicButtonText));
        ActualizeTopicCommand?.RaiseCanExecuteChanged();
    }

    private void SetActualizeStep(string resourceKey)
    {
        _topicActualizeStep = resourceKey.Length > 0 ? L[resourceKey] : string.Empty;
        OnPropertyChanged(nameof(ActualizeTopicButtonText));
    }

    private async Task ActualizeTopicAsync()
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

            bool rebuildAll = !summaryOutdated && !graphOutdated;

            // 2. «Сводка»: the graph is built from it, so it goes first.
            if (summaryOutdated || rebuildAll)
            {
                SetActualizeStep("TopicActualizeSummary");
                if (!await RefreshTopicKnowledgeAsync(topic))
                {
                    return;
                }
            }

            // 3. Graph.
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
